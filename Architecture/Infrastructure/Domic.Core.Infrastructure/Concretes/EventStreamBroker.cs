﻿#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

using System.Reflection;
using System.Text;
using Confluent.Kafka;
using Domic.Core.Common.ClassConsts;
using Domic.Core.Common.ClassEnums;
using Domic.Core.Domain.Contracts.Interfaces;
using Domic.Core.Domain.Entities;
using Domic.Core.Domain.Enumerations;
using Domic.Core.Infrastructure.Extensions;
using Domic.Core.UseCase.Attributes;
using Domic.Core.UseCase.Contracts.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using NSubstitute.Exceptions;
using Polly;
using Environment = System.Environment;

namespace Domic.Core.Infrastructure.Concretes;

public class EventStreamBroker(
    ISerializer serializer, IServiceProvider serviceProvider, IHostEnvironment hostEnvironment, IDateTime dateTime,
    IGlobalUniqueIdGenerator globalUniqueIdGenerator, IServiceScopeFactory serviceScopeFactory
) : IEventStreamBroker
{
    private static object _lock = new();
    
    #region Consts

    private const string GroupIdKey = "GroupId";
    private const string CountOfRetryKey = "CountOfRetry";

    #endregion
    
    public string NameOfAction { get; set; }
    public string NameOfService { get; set; }

    #region StructureOfMessage

    public async Task PublishAsync<TMessage>(string topic, TMessage message, Dictionary<string, string> headers = default,
        CancellationToken cancellationToken = default
    ) where TMessage : class
    {
        var config = new ProducerConfig {
            BootstrapServers = Environment.GetEnvironmentVariable("E-Kafka-Host"),
            SaslUsername = Environment.GetEnvironmentVariable("E-Kafka-Username"),
            SaslPassword = Environment.GetEnvironmentVariable("E-Kafka-Password")
        };

        var retryPolicy = Policy.Handle<Exception>()
            .WaitAndRetryAsync(5, _ => TimeSpan.FromSeconds(3),
                (exception, timeSpan, context) => throw exception
            );
        
        await retryPolicy.ExecuteAsync(async () => {
            
            using var producer = new ProducerBuilder<string, string>(config).Build();
        
            var kafkaHeaders = new Headers();

            foreach (var header in headers)
                kafkaHeaders.Add(header.Key, Encoding.UTF8.GetBytes(header.Value));

            kafkaHeaders.Add(GroupIdKey, Encoding.UTF8.GetBytes(""));
            kafkaHeaders.Add(CountOfRetryKey, Encoding.UTF8.GetBytes("0"));
            
            await producer.ProduceAsync(
                topic,
                new Message<string, string> {
                    Key = message.GetType().Name, Value = serializer.Serialize(message), Headers = kafkaHeaders
                },
                cancellationToken
            );
            
        });
    }
    
    public void SubscribeMessage(string topic, CancellationToken cancellationToken)
    {
        var useCaseTypes = Assembly.Load(new AssemblyName("Domic.UseCase")).GetTypes();

        var config = new ConsumerConfig {
            BootstrapServers = Environment.GetEnvironmentVariable("E-Kafka-Host"),
            SaslUsername = Environment.GetEnvironmentVariable("E-Kafka-Username"),
            SaslPassword = Environment.GetEnvironmentVariable("E-Kafka-Password"),
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            GroupId = $"{NameOfService}-{topic}"
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        
        consumer.Subscribe(topic);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var consumeResult = consumer.Consume(cancellationToken);
                
                _ConsumeNext(useCaseTypes, topic, consumer, consumeResult);
            }
            catch (Exception e)
            {
                e.FileLogger(hostEnvironment, dateTime);

                e.ElasticStackExceptionLogger(hostEnvironment, globalUniqueIdGenerator, dateTime,
                    NameOfService, NameOfAction
                );
            }
        }
    }

    public void SubscribeMessageAsynchronously(string topic, CancellationToken cancellationToken)
    {
        var useCaseTypes = Assembly.Load(new AssemblyName("Domic.UseCase")).GetTypes();

        var config = new ConsumerConfig {
            BootstrapServers = Environment.GetEnvironmentVariable("E-Kafka-Host"),
            SaslUsername = Environment.GetEnvironmentVariable("E-Kafka-Username"),
            SaslPassword = Environment.GetEnvironmentVariable("E-Kafka-Password"),
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            GroupId = $"{NameOfService}-{topic}"
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        
        consumer.Subscribe(topic);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var consumeResult = consumer.Consume(cancellationToken);

                _ConsumeNextAsync(useCaseTypes, topic, consumer, consumeResult, cancellationToken);
            }
            catch (Exception e)
            {
                e.FileLogger(hostEnvironment, dateTime);

                e.ElasticStackExceptionLogger(hostEnvironment, globalUniqueIdGenerator, dateTime,
                    NameOfService, NameOfAction
                );
            }
        }
    }

    #endregion

    public Task PublishAsync(CancellationToken cancellationToken)
    {
        //just one worker ( Task ) in current machine ( instance ) can process outbox events => lock
        lock (_lock)
        {
            //ScopeServices Trigger
            using IServiceScope serviceScope = serviceScopeFactory.CreateScope();

            var commandUnitOfWork =
                serviceScope.ServiceProvider.GetRequiredService(_GetTypeOfUnitOfWork(TransactionType.Command)) as ICoreCommandUnitOfWork;

            var redisCache = serviceScope.ServiceProvider.GetRequiredService<IInternalDistributedCache>();
            var eventCommandRepository = serviceScope.ServiceProvider.GetRequiredService<IEventCommandRepository>();
            
            try
            {
                var eventLocks = new List<string>();
                
                commandUnitOfWork.Transaction();
                
                foreach (Event targetEvent in eventCommandRepository.FindAllWithOrdering(Order.Date))
                {
                    #region DistributedLock

                    var lockEventKey = $"LockEventId-{targetEvent.Id}";
                    
                    //ReleaseLock
                    redisCache.DeleteKey(lockEventKey);
                    
                    //AcquireLock
                    var lockEventSuccessfully = redisCache.SetCacheValue(
                        new KeyValuePair<string, string>(lockEventKey, targetEvent.Id), CacheSetType.NotExists
                    );

                    #endregion

                    if (lockEventSuccessfully)
                    {
                        eventLocks.Add(lockEventKey);
                        
                        if (targetEvent.IsActive == IsActive.Active)
                        {
                            //_EventPublishHandler(channel, targetEvent);

                            var nowDateTime        = DateTime.Now;
                            var nowPersianDateTime = dateTime.ToPersianShortDate(nowDateTime);

                            targetEvent.IsActive              = IsActive.InActive;
                            targetEvent.UpdatedAt_EnglishDate = nowDateTime;
                            targetEvent.UpdatedAt_PersianDate = nowPersianDateTime;

                            eventCommandRepository.Change(targetEvent);
                        }
                        else
                            eventCommandRepository.Remove(targetEvent);
                    }
                }

                commandUnitOfWork.Commit();
                
                //ReleaseLocks
                eventLocks.ForEach(@event => redisCache.DeleteKey(@event));
            }
            catch (Exception e)
            {
                e.FileLogger(hostEnvironment, dateTime);
                
                e.ElasticStackExceptionLogger(hostEnvironment, globalUniqueIdGenerator, dateTime, 
                    NameOfService, NameOfAction
                );
                
                // e.CentralExceptionLogger(hostEnvironment, globalUniqueIdGenerator, this, dateTime, NameOfService, 
                //     NameOfAction
                // );

                commandUnitOfWork?.Rollback();
            }
            finally
            {
                
            }
        }

        return Task.CompletedTask;
    }
    
    /*---------------------------------------------------------------*/

    private void _ConsumeNext(Type[] useCaseTypes, string topic, IConsumer<string, string> consumer,
        ConsumeResult<string, string> consumeResult
    )
    {
        IUnitOfWork unitOfWork = default;
        int countOfRetryValue  = default;
        object payloadBody     = default;
        
        try
        {
            var groupIdHeader = consumeResult.Message.Headers.First(h => h.Key == GroupIdKey);
            var countOfRetryHeader = consumeResult.Message.Headers.First(h => h.Key == CountOfRetryKey);
            var groupIdValue = Encoding.UTF8.GetString(groupIdHeader.GetValueBytes());
            
            countOfRetryValue = Convert.ToInt32( Encoding.UTF8.GetString(countOfRetryHeader.GetValueBytes()) );
            
            var processingConditions = (
                string.IsNullOrEmpty(groupIdValue) || (
                    groupIdValue == $"{NameOfService}-{topic}" && countOfRetryValue > 0
                )
            );

            if (processingConditions)
            {
                var targetConsumerMessageStreamHandlerType = useCaseTypes.FirstOrDefault(
                    type => type.GetInterfaces().Any(
                        i => i.IsGenericType &&
                             i.GetGenericTypeDefinition() == typeof(IConsumerMessageStreamHandler<>) &&
                             i.GetGenericArguments().Any(arg => arg.Name.Equals(consumeResult.Message.Key))
                    )
                );

                if (targetConsumerMessageStreamHandlerType is not null)
                {
                    var messageStreamType =
                        targetConsumerMessageStreamHandlerType.GetInterfaces()
                                                              .Select(i => i.GetGenericArguments()[0])
                                                              .FirstOrDefault();
 
                    var fullContractOfConsumerType =
                        typeof(IConsumerMessageStreamHandler<>).MakeGenericType(messageStreamType);
 
                    var messageStreamHandler = serviceProvider.GetRequiredService(fullContractOfConsumerType);
 
                    var messageStreamHandlerType = messageStreamHandler.GetType();
 
                    payloadBody = JsonConvert.DeserializeObject(consumeResult.Message.Value, messageStreamType);
 
                    var messageStreamHandlerMethod =
                        messageStreamHandlerType.GetMethod("Handle") ?? throw new Exception("Handle function not found !");
 
                    var retryAttr =
                        messageStreamHandlerMethod.GetCustomAttribute(typeof(WithMaxRetryAttribute)) as WithMaxRetryAttribute;
                     
                    if (countOfRetryValue > retryAttr.Count)
                    {
                        if (retryAttr.HasAfterMaxRetryHandle)
                        {
                            var afterMaxRetryHandlerMethod =
                                messageStreamHandlerType.GetMethod("AfterMaxRetryHandle") ?? throw new Exception("AfterMaxRetryHandle function not found !");
                     
                            afterMaxRetryHandlerMethod.Invoke(messageStreamHandler, new[] { payloadBody });
                        }
                    }
                    else
                    {
                        var transactionConfig =
                            messageStreamHandlerMethod.GetCustomAttribute(typeof(TransactionConfigAttribute)) as TransactionConfigAttribute;
            
                        if(transactionConfig is null)
                            throw new Exception("Must be used transaction config attribute!");

                        var consumerEventQueryRepository = serviceProvider.GetRequiredService<IConsumerEventQueryRepository>();

                        var messageId =
                            payloadBody.GetType().GetProperty("Id",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly
                            ).GetValue(payloadBody);
            
                        //todo: should be used [CancelationToken] from this method ( _MessageOfQueueHandle )
                        var consumerEventQuery = consumerEventQueryRepository.FindByIdAsync(messageId, default).GetAwaiter().GetResult();

                        if (consumerEventQuery is null)
                        {
                            unitOfWork = serviceProvider.GetRequiredService(_GetTypeOfUnitOfWork(transactionConfig.Type)) as IUnitOfWork;
 
                            unitOfWork.Transaction(transactionConfig.IsolationLevel);
                             
                            #region IdempotentConsumerPattern
            
                            var nowDateTime = DateTime.Now;

                            consumerEventQuery = new ConsumerEventQuery {
                                Id = messageId.ToString(),
                                Type = consumeResult.Message.Key,
                                CountOfRetry = countOfRetryValue,
                                CreatedAt_EnglishDate = nowDateTime,
                                CreatedAt_PersianDate = dateTime.ToPersianShortDate(nowDateTime)
                            };

                            consumerEventQueryRepository.Add(consumerEventQuery);

                            #endregion
 
                            messageStreamHandlerMethod.Invoke(messageStreamHandler, new[] { payloadBody });
 
                            unitOfWork.Commit();
                        }
                    }
                }
            }
            
            consumer.Commit(consumeResult);
        }
        catch (Exception e)
        {
            e.FileLogger(hostEnvironment, dateTime);

            e.ElasticStackExceptionLogger(hostEnvironment, globalUniqueIdGenerator, dateTime,
                NameOfService, NameOfAction
            );

            unitOfWork?.Rollback();
            
            var isSuccessRetry = _RetryEventOrMessageOfTopic(topic, payloadBody, countOfRetryValue);
            
            if(isSuccessRetry)
                consumer.Commit(consumeResult);
        }
    }
    
    private async Task _ConsumeNextAsync(Type[] useCaseTypes, string topic, IConsumer<string, string> consumer,
        ConsumeResult<string, string> consumeResult, CancellationToken cancellationToken
    )
    {
        IUnitOfWork unitOfWork = default;
        int countOfRetryValue  = default;
        object payloadBody     = default;
        
        try
        {
            var groupIdHeader = consumeResult.Message.Headers.First(h => h.Key == GroupIdKey);
            var countOfRetryHeader = consumeResult.Message.Headers.First(h => h.Key == CountOfRetryKey);
            var groupIdValue = Encoding.UTF8.GetString(groupIdHeader.GetValueBytes());
            
            countOfRetryValue = Convert.ToInt32( Encoding.UTF8.GetString(countOfRetryHeader.GetValueBytes()) );
            
            var processingConditions = (
                string.IsNullOrEmpty(groupIdValue) || (
                    groupIdValue == $"{NameOfService}-{topic}" && countOfRetryValue > 0
                )
            );

            if (processingConditions)
            {
                var targetConsumerMessageStreamHandlerType = useCaseTypes.FirstOrDefault(
                    type => type.GetInterfaces().Any(
                        i => i.IsGenericType &&
                             i.GetGenericTypeDefinition() == typeof(IConsumerMessageStreamHandler<>) &&
                             i.GetGenericArguments().Any(arg => arg.Name.Equals(consumeResult.Message.Key))
                    )
                );

                if (targetConsumerMessageStreamHandlerType is not null)
                {
                    var messageStreamType =
                        targetConsumerMessageStreamHandlerType.GetInterfaces()
                                                              .Select(i => i.GetGenericArguments()[0])
                                                              .FirstOrDefault();

                    var fullContractOfConsumerType =
                        typeof(IConsumerMessageStreamHandler<>).MakeGenericType(messageStreamType);

                    var messageStreamHandler = serviceProvider.GetRequiredService(fullContractOfConsumerType);

                    var messageStreamHandlerType = messageStreamHandler.GetType();

                    payloadBody = JsonConvert.DeserializeObject(consumeResult.Message.Value, messageStreamType);

                    var messageStreamHandlerMethod =
                        messageStreamHandlerType.GetMethod("Handle") ?? throw new Exception("Handle function not found !");

                    var retryAttr =
                        messageStreamHandlerMethod.GetCustomAttribute(typeof(WithMaxRetryAttribute)) as WithMaxRetryAttribute;
                     
                    if (countOfRetryValue > retryAttr.Count)
                    {
                        if (retryAttr.HasAfterMaxRetryHandle)
                        {
                            var afterMaxRetryHandlerMethod =
                                messageStreamHandlerType.GetMethod("AfterMaxRetryHandle") ?? throw new Exception("AfterMaxRetryHandle function not found !");
                     
                            afterMaxRetryHandlerMethod.Invoke(messageStreamHandler, new[] { payloadBody });
                        }
                    }
                    else
                    {
                        var transactionConfig =
                            messageStreamHandlerMethod.GetCustomAttribute(typeof(TransactionConfigAttribute)) as TransactionConfigAttribute;
            
                        if(transactionConfig is null)
                            throw new Exception("Must be used transaction config attribute!");

                        var consumerEventQueryRepository = serviceProvider.GetRequiredService<IConsumerEventQueryRepository>();

                        var messageId =
                            payloadBody.GetType().GetProperty("Id",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly
                            ).GetValue(payloadBody);
            
                        //todo: should be used [CancelationToken] from this method ( _MessageOfQueueHandle )
                        var consumerEventQuery = await consumerEventQueryRepository.FindByIdAsync(messageId, cancellationToken);

                        if (consumerEventQuery is null)
                        {
                            unitOfWork = serviceProvider.GetRequiredService(_GetTypeOfUnitOfWork(transactionConfig.Type)) as IUnitOfWork;

                            await unitOfWork.TransactionAsync(transactionConfig.IsolationLevel, cancellationToken);
                             
                            #region IdempotentConsumerPattern
            
                            var nowDateTime = DateTime.Now;

                            consumerEventQuery = new ConsumerEventQuery {
                                Id = messageId.ToString(),
                                Type = consumeResult.Message.Key,
                                CountOfRetry = countOfRetryValue,
                                CreatedAt_EnglishDate = nowDateTime,
                                CreatedAt_PersianDate = dateTime.ToPersianShortDate(nowDateTime)
                            };

                            consumerEventQueryRepository.Add(consumerEventQuery);

                            #endregion

                            await (Task)messageStreamHandlerMethod.Invoke(messageStreamHandler, new[] { payloadBody, cancellationToken });

                            await unitOfWork.CommitAsync(cancellationToken);
                        }
                    }
                }
            }
            
            consumer.Commit(consumeResult);
        }
        catch (Exception e)
        {
            e.FileLogger(hostEnvironment, dateTime);

            e.ElasticStackExceptionLogger(hostEnvironment, globalUniqueIdGenerator, dateTime,
                NameOfService, NameOfAction
            );

            unitOfWork?.Rollback();
            
            var isSuccessRetry =
                await _RetryEventOrMessageOfTopicAsync(topic, payloadBody, countOfRetryValue, cancellationToken);
            
            if(isSuccessRetry) 
                consumer.Commit(consumeResult);
        }
    }
    
    private Type _GetTypeOfUnitOfWork(TransactionType transactionType)
    {
        var domainTypes = Assembly.Load(new AssemblyName("Domic.Domain")).GetTypes();

        return transactionType switch {
            TransactionType.Query => 
                domainTypes.FirstOrDefault(type => type.GetInterfaces().Any(i => i == typeof(ICoreQueryUnitOfWork))),
            TransactionType.Command => 
                domainTypes.FirstOrDefault(type => type.GetInterfaces().Any(i => i == typeof(ICoreCommandUnitOfWork))),
            _ => throw new ArgumentNotFoundException("Must be defined transaction type!")
        };
    }
    
    private bool _RetryEventOrMessageOfTopic(string topic, object payload, int countOfRetry)
    {
        try
        {
            var config = new ProducerConfig {
                BootstrapServers = Environment.GetEnvironmentVariable("E-Kafka-Host"),
                SaslUsername = Environment.GetEnvironmentVariable("E-Kafka-Username"),
                SaslPassword = Environment.GetEnvironmentVariable("E-Kafka-Password")
            };
            
            var kafkaHeaders = new Headers {
                { GroupIdKey, Encoding.UTF8.GetBytes( $"{NameOfService}-{topic}" ) },
                { CountOfRetryKey, Encoding.UTF8.GetBytes( $"{countOfRetry}" ) }
            };
        
            
            Policy.Handle<Exception>()
                  .WaitAndRetry(5, _ => TimeSpan.FromSeconds(3), (exception, timeSpan, context) => throw exception)
                  .Execute(() => {
                      
                      using var producer = new ProducerBuilder<string, string>(config).Build();

                      producer.Produce(
                          topic,
                          new Message<string, string> {
                              Key = payload.GetType().Name, Value = serializer.Serialize(payload), Headers = kafkaHeaders
                          }
                      );
                      
                  });
        }
        catch (Exception e)
        {
            e.FileLogger(hostEnvironment, dateTime);
            
            e.ElasticStackExceptionLogger(hostEnvironment, globalUniqueIdGenerator, dateTime, 
                NameOfService, NameOfAction
            );

            return false;
        }

        return true;
    }
    
    private async Task<bool> _RetryEventOrMessageOfTopicAsync(string topic, object payload, int countOfRetry,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var config = new ProducerConfig {
                BootstrapServers = Environment.GetEnvironmentVariable("E-Kafka-Host"),
                SaslUsername = Environment.GetEnvironmentVariable("E-Kafka-Username"),
                SaslPassword = Environment.GetEnvironmentVariable("E-Kafka-Password")
            };
            
            var kafkaHeaders = new Headers {
                { GroupIdKey, Encoding.UTF8.GetBytes( $"{NameOfService}-{topic}" ) },
                { CountOfRetryKey, Encoding.UTF8.GetBytes( $"{countOfRetry}" ) }
            };
            
            await Policy.Handle<Exception>()
                        .WaitAndRetryAsync(5, _ => TimeSpan.FromSeconds(3), (exception, timeSpan, context) => throw exception)
                        .ExecuteAsync(async () => {

                            using var producer = new ProducerBuilder<string, string>(config).Build();
                
                            await producer.ProduceAsync(
                                topic,
                                new Message<string, string> {
                                    Key = payload.GetType().Name, Value = serializer.Serialize(payload), Headers = kafkaHeaders
                                },
                                cancellationToken
                            );

                        });
        }
        catch (Exception e)
        {
            e.FileLogger(hostEnvironment, dateTime);
            
            e.ElasticStackExceptionLogger(hostEnvironment, globalUniqueIdGenerator, dateTime, 
                NameOfService, NameOfAction
            );

            return false;
        }

        return true;
    }
}