﻿using System.Reflection;
using System.Text;
using Domic.Core.Common.ClassConsts;
using Domic.Core.Common.ClassEnums;
using Domic.Core.Domain.Attributes;
using Domic.Core.Domain.Contracts.Interfaces;
using Domic.Core.Domain.Entities;
using Domic.Core.Domain.Enumerations;
using Domic.Core.Common.ClassExtensions;
using Domic.Core.Common.ClassModels;
using Domic.Core.Infrastructure.Extensions;
using Domic.Core.UseCase.Attributes;
using Domic.Core.UseCase.Contracts.Interfaces;
using Domic.Core.UseCase.DTOs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using NSubstitute.Exceptions;
using NSubstitute.Extensions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Domic.Core.Infrastructure.Concretes;

public class MessageBroker : IMessageBroker
{
    private static object _lock = new();

    private readonly IConnection _connection;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IDateTime _dateTime;
    private readonly IGlobalUniqueIdGenerator _globalUniqueIdGenerator;
    private readonly IConfiguration _configuration;

    public MessageBroker(IConfiguration configuration, IHostEnvironment hostEnvironment, 
        IServiceScopeFactory serviceScopeFactory, IDateTime dateTime, IGlobalUniqueIdGenerator globalUniqueIdGenerator
    )
    {
        _hostEnvironment = hostEnvironment;
        _serviceScopeFactory = serviceScopeFactory;
        _dateTime = dateTime;
        _globalUniqueIdGenerator = globalUniqueIdGenerator;
        _configuration = configuration;

        var factory = new ConnectionFactory {
            HostName = configuration.GetExternalRabbitHostName(),
            UserName = configuration.GetExternalRabbitUsername(),
            Password = configuration.GetExternalRabbitPassword(),
            Port     = configuration.GetExternalRabbitPort() 
        };

        factory.DispatchConsumersAsync = configuration.GetValue<bool>("IsExternalBrokerConsumingAsync");
        
        _connection = factory.CreateConnection();
    }

    public string NameOfAction  { get; set; }
    public string NameOfService { get; set; }

    #region MessageStructure

    public void Publish<TMessage>(MessageBrokerDto<TMessage> messageBroker) where TMessage : class
    {
        using var channel = _connection.CreateModel();

        switch (messageBroker.ExchangeType)
        {
            case Exchange.Direct :
                channel.PublishMessageToDirectExchange(
                    messageBroker.Message.Serialize(), messageBroker.Exchange, messageBroker.Route, 
                    messageBroker.Headers
                );
                break;

            case Exchange.FanOut :
                channel.PublishMessageToFanOutExchange(
                    messageBroker.Message.Serialize(), messageBroker.Exchange
                );
                break;

            case Exchange.Unknown :
                channel.PublishMessage(messageBroker.Message.Serialize(), messageBroker.Queue);
                break;

            default: throw new ArgumentOutOfRangeException();
        }
    }

    public void Subscribe<TMessage>(string queue) where TMessage : class
    {
        try
        {
            var channel = _connection.CreateModel();
            
            EventingBasicConsumer consumer = new(channel);

            consumer.Received += (sender, args) => {
                
                //ScopeServices trigger
                using IServiceScope serviceScope = _serviceScopeFactory.CreateScope();
                
                var message = Encoding.UTF8.GetString(args.Body.ToArray()).DeSerialize<TMessage>();

                _MessageOfQueueHandle(channel, args, message, serviceScope.ServiceProvider);
                
            };
            
            channel.BasicConsume(queue: queue, consumer: consumer);
        }
        catch (Exception e)
        {
            e.FileLogger(_hostEnvironment, _dateTime);
            
            e.ElasticStackExceptionLogger(_hostEnvironment, _globalUniqueIdGenerator, _dateTime, 
                NameOfService, NameOfAction
            );
        }
    }

    public void Subscribe(string queue, Type messageType)
    {
        try
        {
            var channel = _connection.CreateModel();
            
            EventingBasicConsumer consumer = new(channel);

            consumer.Received += (sender, args) => {
                
                //ScopeServices trigger
                using IServiceScope serviceScope = _serviceScopeFactory.CreateScope();
                
                var message = Encoding.UTF8.GetString(args.Body.ToArray()).DeSerialize(messageType);

                _MessageOfQueueHandle(channel, args, message, serviceScope.ServiceProvider);
                
            };
            
            channel.BasicConsume(queue: queue, consumer: consumer);
        }
        catch (Exception e)
        {
            e.FileLogger(_hostEnvironment, _dateTime);
            
            e.ElasticStackExceptionLogger(_hostEnvironment, _globalUniqueIdGenerator, _dateTime, 
                NameOfService, NameOfAction
            );
        }
    }

    public void SubscribeAsynchronously<TMessage>(string queue, CancellationToken cancellationToken) where TMessage : class
    {
        try
        {
            var channel = _connection.CreateModel();
            
            AsyncEventingBasicConsumer consumer = new(channel);

            consumer.Received += async (sender, args) => {
                
                //ScopeServices trigger
                using IServiceScope serviceScope = _serviceScopeFactory.CreateScope();
                
                var message = Encoding.UTF8.GetString(args.Body.ToArray()).DeSerialize<TMessage>();

                await _MessageOfQueueHandleAsync(channel, args, message, serviceScope.ServiceProvider, cancellationToken);
                
            };
            
            channel.BasicConsume(queue: queue, consumer: consumer);
        }
        catch (Exception e)
        {
            e.FileLogger(_hostEnvironment, _dateTime);
            
            e.ElasticStackExceptionLogger(_hostEnvironment, _globalUniqueIdGenerator, _dateTime, 
                NameOfService, NameOfAction
            );
        }
    }

    public void SubscribeAsynchronously(string queue, Type messageType, CancellationToken cancellationToken)
    {
        try
        {
            var channel = _connection.CreateModel();
            
            AsyncEventingBasicConsumer consumer = new(channel);

            consumer.Received += async (sender, args) => {
                
                //ScopeServices trigger
                using IServiceScope serviceScope = _serviceScopeFactory.CreateScope();
                
                var message = Encoding.UTF8.GetString(args.Body.ToArray()).DeSerialize(messageType);

                await _MessageOfQueueHandleAsync(channel, args, message, serviceScope.ServiceProvider, cancellationToken);
                
            };
            
            channel.BasicConsume(queue: queue, consumer: consumer);
        }
        catch (Exception e)
        {
            e.FileLogger(_hostEnvironment, _dateTime);
            
            e.ElasticStackExceptionLogger(_hostEnvironment, _globalUniqueIdGenerator, _dateTime, 
                NameOfService, NameOfAction
            );
        }
    }

    #endregion

    #region EventStructure
    
    public void Publish()
    {
        //just one worker ( Task ) in current machine ( instance ) can process outbox events => lock
        lock (_lock)
        {
            //ScopeServices trigger
            using IServiceScope serviceScope = _serviceScopeFactory.CreateScope();

            var commandUnitOfWork =
                serviceScope.ServiceProvider.GetRequiredService(_GetTypeOfCommandUnitOfWork()) as ICoreCommandUnitOfWork;

            var redisCache = serviceScope.ServiceProvider.GetRequiredService<IInternalDistributedCache>();
            var eventCommandRepository = serviceScope.ServiceProvider.GetRequiredService<IEventCommandRepository>();

            IModel channel = default;
            
            try
            {
                channel = _connection.CreateModel();
                
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
                            _EventPublishHandler(channel, targetEvent);

                            var nowDateTime        = DateTime.Now;
                            var nowPersianDateTime = _dateTime.ToPersianShortDate(nowDateTime);

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
                e.FileLogger(_hostEnvironment, _dateTime);
                
                e.ElasticStackExceptionLogger(_hostEnvironment, _globalUniqueIdGenerator, _dateTime, 
                    NameOfService, NameOfAction
                );
                
                e.CentralExceptionLogger(_hostEnvironment, _globalUniqueIdGenerator, this, _dateTime, NameOfService, 
                    NameOfAction
                );

                commandUnitOfWork?.Rollback();
            }
            finally
            {
                try
                {
                    channel?.Dispose();
                }
                catch (Exception e){}
            }
        }
    }

    public void Subscribe(string queue)
    {
        try
        {
            var channel = _connection.CreateModel();
            
            var consumer = new EventingBasicConsumer(channel);

            #region Throttle

            var queueConfig = _configuration.GetSection("QueueConfig").Get<QueueConfig>();

            var queueThrottle = queueConfig.Throttles.FirstOrDefault(throttle => throttle.Queue.Equals(queue));
            
            if(queueThrottle is not null && queueThrottle.Active)
                channel.BasicQos(queueThrottle.Size, queueThrottle.Limitation, queueThrottle.IsGlobally);

            #endregion

            consumer.Received += (sender, args) => {
                
                //ScopeServices trigger
                using IServiceScope serviceScope = _serviceScopeFactory.CreateScope();
                
                var @event = Encoding.UTF8.GetString(args.Body.ToArray()).DeSerialize<Event>();
                
                _EventOfQueueHandle(channel, args, @event, NameOfService, serviceScope.ServiceProvider);
                
            };

            channel.BasicConsume(queue: queue, consumer: consumer);
        }
        catch (Exception e)
        {
            e.FileLogger(_hostEnvironment, _dateTime);
            
            e.ElasticStackExceptionLogger(_hostEnvironment, _globalUniqueIdGenerator, _dateTime, 
                NameOfService, NameOfAction
            );
        }
    }

    public void SubscribeAsynchronously(string queue, CancellationToken cancellationToken)
    {
        try
        {
            var channel = _connection.CreateModel();
            
            var consumer = new AsyncEventingBasicConsumer(channel);

            consumer.Received += async (sender, args) => {
                
                //ScopeServices trigger
                using IServiceScope serviceScope = _serviceScopeFactory.CreateScope();
                
                var @event = Encoding.UTF8.GetString(args.Body.ToArray()).DeSerialize<Event>();
                
               await _EventOfQueueHandleAsync(channel, args, @event, NameOfService, serviceScope.ServiceProvider, 
                   cancellationToken
               );
                
            };

            channel.BasicConsume(queue: queue, consumer: consumer);
        }
        catch (Exception e)
        {
            e.FileLogger(_hostEnvironment, _dateTime);
            
            e.ElasticStackExceptionLogger(_hostEnvironment, _globalUniqueIdGenerator, _dateTime, 
                NameOfService, NameOfAction
            );
        }
    }

    #endregion

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        _connection.Close();
        _connection.Dispose();
        
        return ValueTask.CompletedTask;
    }

    /*---------------------------------------------------------------*/

    private void _EventPublishHandler(IModel channel, Event @event)
    {
        var nameOfEvent = @event.Type;

        var domainTypes = Assembly.Load(new AssemblyName("Domic.Domain")).GetTypes();
        
        var typeOfEvents = domainTypes.Where(
            type => type.BaseType?.GetInterfaces().Any(i => i == typeof(IDomainEvent)) ?? false
        );

        var typeOfEvent = typeOfEvents.FirstOrDefault(type => type.Name.Equals(nameOfEvent));

        var messageBroker = typeOfEvent.GetCustomAttribute(typeof(MessageBrokerAttribute)) as MessageBrokerAttribute;

        switch (messageBroker.ExchangeType)
        {
            case Exchange.Direct :
                channel.PublishMessageToDirectExchange(
                    @event.Serialize(), messageBroker.Exchange, messageBroker.Route
                );
            break;
            
            case Exchange.FanOut :
                channel.PublishMessageToFanOutExchange(
                    @event.Serialize(), messageBroker.Exchange
                );
            break;

            default : throw new ArgumentOutOfRangeException();
        }
    }

    private void _MessageOfQueueHandle<TMessage>(IModel channel, BasicDeliverEventArgs args, TMessage message,
        IServiceProvider serviceProvider
    ) where TMessage : class
    {
        IUnitOfWork unitOfWork = null;

        try
        {
            var messageBusHandler     = serviceProvider.GetRequiredService(typeof(IConsumerMessageBusHandler<TMessage>));
            var messageBusHandlerType = messageBusHandler.GetType();
            
            var messageBusHandlerMethod =
                messageBusHandlerType.GetMethod("Handle") ?? throw new Exception("Handle function not found !");

            var retryAttr =
                messageBusHandlerMethod.GetCustomAttribute(typeof(WithMaxRetryAttribute)) as WithMaxRetryAttribute;

            var maxRetryInfo = _IsMaxRetryMessage(args, retryAttr);
            
            if (maxRetryInfo.result)
            {
                if (retryAttr.HasAfterMaxRetryHandle)
                {
                    var afterMaxRetryHandlerMethod =
                        messageBusHandlerType.GetMethod("AfterMaxRetryHandle") ?? throw new Exception("AfterMaxRetryHandle function not found !");
                    
                    afterMaxRetryHandlerMethod.Invoke(messageBusHandler, new object[] { message });
                }
            }
            else
            {
                var transactionConfig =
                    messageBusHandlerMethod.GetCustomAttribute(typeof(TransactionConfigAttribute)) as TransactionConfigAttribute;
                
                if(transactionConfig is null)
                    throw new Exception("Must be used transaction config attribute!");

                unitOfWork =
                    serviceProvider.GetRequiredService(_GetTypeOfUnitOfWork(transactionConfig.Type)) as IUnitOfWork;

                var consumerEventQueryRepository = serviceProvider.GetRequiredService<IConsumerEventQueryRepository>();

                var messageId =
                    message.GetType().GetProperty("Id", 
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly
                    ).GetValue(message);
                
                var consumerEventQuery = consumerEventQueryRepository.FindById(messageId);

                if (consumerEventQuery is null)
                {
                    unitOfWork.Transaction(transactionConfig.IsolationLevel);
                    
                    #region IdempotentConsumerPattern
                
                    var nowDateTime = DateTime.Now;

                    consumerEventQuery = new ConsumerEventQuery {
                        Id = messageId.ToString(),
                        Type = nameof(message),
                        CountOfRetry = maxRetryInfo.countOfRetry,
                        CreatedAt_EnglishDate = nowDateTime,
                        CreatedAt_PersianDate = _dateTime.ToPersianShortDate(nowDateTime)
                    };

                    consumerEventQueryRepository.Add(consumerEventQuery);

                    #endregion
                
                    messageBusHandlerMethod.Invoke(messageBusHandler, new object[] { message });
                
                    unitOfWork.Commit();

                    _CleanCache(messageBusHandlerMethod, serviceProvider);
                }
            }
            
            _TrySendAckMessage(channel, args);
        }
        catch (Exception e)
        {
            e.FileLogger(_hostEnvironment, _dateTime);
            
            e.ElasticStackExceptionLogger(_hostEnvironment, _globalUniqueIdGenerator, _dateTime, 
                NameOfService, NameOfAction
            );
            
            unitOfWork?.Rollback();

            _RequeueMessageAsDeadLetter(channel, args);
        }
    }
    
    private async Task _MessageOfQueueHandleAsync<TMessage>(IModel channel, BasicDeliverEventArgs args, TMessage message,
        IServiceProvider serviceProvider, CancellationToken cancellationToken
    ) where TMessage : class
    {
        IUnitOfWork unitOfWork = null;

        try
        {
            var messageBusHandler     = serviceProvider.GetRequiredService(typeof(IConsumerMessageBusHandler<TMessage>));
            var messageBusHandlerType = messageBusHandler.GetType();
            
            var messageBusHandlerMethod =
                messageBusHandlerType.GetMethod("HandleAsync") ?? throw new Exception("HandleAsync function not found !");

            var retryAttr =
                messageBusHandlerMethod.GetCustomAttribute(typeof(WithMaxRetryAttribute)) as WithMaxRetryAttribute;

            var maxRetryInfo = _IsMaxRetryMessage(args, retryAttr);
            
            if (maxRetryInfo.result)
            {
                if (retryAttr.HasAfterMaxRetryHandle)
                {
                    var afterMaxRetryHandlerMethod =
                        messageBusHandlerType.GetMethod("AfterMaxRetryHandleAsync") ?? throw new Exception("AfterMaxRetryHandleAsync function not found !");
                    
                    await (Task)afterMaxRetryHandlerMethod.Invoke(messageBusHandler, new object[] { message, cancellationToken });
                }
            }
            else
            {
                var transactionConfig =
                    messageBusHandlerMethod.GetCustomAttribute(typeof(TransactionConfigAttribute)) as TransactionConfigAttribute;

                if(transactionConfig is null)
                    throw new Exception("Must be used transaction config attribute!");
                
                unitOfWork =
                    serviceProvider.GetRequiredService(_GetTypeOfUnitOfWork(transactionConfig.Type)) as IUnitOfWork;

                var consumerEventQueryRepository = serviceProvider.GetRequiredService<IConsumerEventQueryRepository>();

                var messageId =
                    message.GetType().GetProperty("Id", 
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly
                    ).GetValue(message);
                
                var consumerEventQuery = await consumerEventQueryRepository.FindByIdAsync(messageId, cancellationToken);

                if (consumerEventQuery is null)
                {
                    await unitOfWork.TransactionAsync(transactionConfig.IsolationLevel, cancellationToken);
                
                    #region IdempotentConsumerPattern
                
                    var nowDateTime = DateTime.Now;

                    consumerEventQuery = new ConsumerEventQuery {
                        Id = messageId.ToString(),
                        Type = nameof(message),
                        CountOfRetry = maxRetryInfo.countOfRetry,
                        CreatedAt_EnglishDate = nowDateTime,
                        CreatedAt_PersianDate = _dateTime.ToPersianShortDate(nowDateTime)
                    };

                    consumerEventQueryRepository.Add(consumerEventQuery);

                    #endregion
                    
                    await (Task)messageBusHandlerMethod.Invoke(messageBusHandler, new object[] { message, cancellationToken });
                
                    await unitOfWork.CommitAsync(cancellationToken);

                    _CleanCache(messageBusHandlerMethod, serviceProvider);
                }
            }
            
            _TrySendAckMessage(channel, args);
        }
        catch (Exception e)
        {
            e.FileLogger(_hostEnvironment, _dateTime);
            
            e.ElasticStackExceptionLogger(_hostEnvironment, _globalUniqueIdGenerator, _dateTime, 
                NameOfService, NameOfAction
            );
            
            if(unitOfWork is not null) await unitOfWork.RollbackAsync(cancellationToken);

            _RequeueMessageAsDeadLetter(channel, args);
        }
    }
    
    private void _MessageOfQueueHandle(IModel channel, BasicDeliverEventArgs args, object message, 
        IServiceProvider serviceProvider
    )
    {
       IUnitOfWork unitOfWork = null;

        try
        {
            var messageType = message.GetType();
            
            var consumerMessageBusHandlerContract =
                typeof(IConsumerMessageBusHandler<>).MakeGenericType(messageType);
                
            var messageBusHandler     = serviceProvider.GetRequiredService(consumerMessageBusHandlerContract);
            var messageBusHandlerType = messageBusHandler.GetType();
            
            var messageBusHandlerMethod =
                messageBusHandlerType.GetMethod("Handle") ?? throw new Exception("Handle function not found !");

            var retryAttr =
                messageBusHandlerMethod.GetCustomAttribute(typeof(WithMaxRetryAttribute)) as WithMaxRetryAttribute;

            var maxRetryInfo = _IsMaxRetryMessage(args, retryAttr);
            
            if (maxRetryInfo.result)
            {
                if (retryAttr.HasAfterMaxRetryHandle)
                {
                    var afterMaxRetryHandlerMethod =
                        messageBusHandlerType.GetMethod("AfterMaxRetryHandle") ?? throw new Exception("AfterMaxRetryHandle function not found !");
                    
                    afterMaxRetryHandlerMethod.Invoke(messageBusHandler, new[] { message });
                }
            }
            else
            {
                var transactionConfig =
                    messageBusHandlerMethod.GetCustomAttribute(typeof(TransactionConfigAttribute)) as TransactionConfigAttribute;

                if(transactionConfig is null)
                    throw new Exception("Must be used transaction config attribute!");
                
                unitOfWork =
                    serviceProvider.GetRequiredService(_GetTypeOfUnitOfWork(transactionConfig.Type)) as IUnitOfWork;

                var consumerEventQueryRepository = serviceProvider.GetRequiredService<IConsumerEventQueryRepository>();

                var messageId =
                    message.GetType().GetProperty("Id", 
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly
                    ).GetValue(message);
                
                var consumerEventQuery = consumerEventQueryRepository.FindById(messageId);

                if (consumerEventQuery is null)
                {
                    unitOfWork.Transaction(transactionConfig.IsolationLevel);
                    
                    #region IdempotentConsumerPattern
                
                    var nowDateTime = DateTime.Now;

                    consumerEventQuery = new ConsumerEventQuery {
                        Id = messageId.ToString(),
                        Type = messageType.Name,
                        CountOfRetry = maxRetryInfo.countOfRetry,
                        CreatedAt_EnglishDate = nowDateTime,
                        CreatedAt_PersianDate = _dateTime.ToPersianShortDate(nowDateTime)
                    };

                    consumerEventQueryRepository.Add(consumerEventQuery);

                    #endregion
                
                    messageBusHandlerMethod.Invoke(messageBusHandler, new[] { message });
                
                    unitOfWork.Commit();

                    _CleanCache(messageBusHandlerMethod, serviceProvider);
                }
            }
            
            _TrySendAckMessage(channel, args);
        }
        catch (Exception e)
        {
            e.FileLogger(_hostEnvironment, _dateTime);
            
            e.ElasticStackExceptionLogger(_hostEnvironment, _globalUniqueIdGenerator, _dateTime, 
                NameOfService, NameOfAction
            );
            
            unitOfWork?.Rollback();

            _RequeueMessageAsDeadLetter(channel, args);
        }
    }
    
    private async Task _MessageOfQueueHandleAsync(IModel channel, BasicDeliverEventArgs args, object message, 
        IServiceProvider serviceProvider, CancellationToken cancellationToken
    )
    {
       IUnitOfWork unitOfWork = null;

        try
        {
            var messageType = message.GetType();
            
            var consumerMessageBusHandlerContract =
                typeof(IConsumerMessageBusHandler<>).MakeGenericType(messageType);
                
            var messageBusHandler     = serviceProvider.GetRequiredService(consumerMessageBusHandlerContract);
            var messageBusHandlerType = messageBusHandler.GetType();
            
            var messageBusHandlerMethod =
                messageBusHandlerType.GetMethod("HandleAsync") ?? throw new Exception("HandleAsync function not found !");

            var retryAttr =
                messageBusHandlerMethod.GetCustomAttribute(typeof(WithMaxRetryAttribute)) as WithMaxRetryAttribute;

            var maxRetryInfo = _IsMaxRetryMessage(args, retryAttr);
            
            if (maxRetryInfo.result)
            {
                if (retryAttr.HasAfterMaxRetryHandle)
                {
                    var afterMaxRetryHandlerMethod =
                        messageBusHandlerType.GetMethod("AfterMaxRetryHandleAsync") ?? throw new Exception("AfterMaxRetryHandleAsync function not found !");
                    
                    await (Task)afterMaxRetryHandlerMethod.Invoke(messageBusHandler, new[] { message, cancellationToken });
                }
            }
            else
            {
                var transactionConfig =
                    messageBusHandlerMethod.GetCustomAttribute(typeof(TransactionConfigAttribute)) as TransactionConfigAttribute;

                if(transactionConfig is null)
                    throw new Exception("Must be used transaction config attribute!");
                
                unitOfWork =
                    serviceProvider.GetRequiredService(_GetTypeOfUnitOfWork(transactionConfig.Type)) as IUnitOfWork;
                
                var consumerEventQueryRepository = serviceProvider.GetRequiredService<IConsumerEventQueryRepository>();

                var messageId =
                    message.GetType().GetProperty("Id", 
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly
                    ).GetValue(message);
                
                var consumerEventQuery = await consumerEventQueryRepository.FindByIdAsync(messageId, cancellationToken);

                if (consumerEventQuery is null)
                {
                    await unitOfWork.TransactionAsync(transactionConfig.IsolationLevel, cancellationToken);
                
                    #region IdempotentConsumerPattern
                
                    var nowDateTime = DateTime.Now;

                    consumerEventQuery = new ConsumerEventQuery {
                        Id = messageId.ToString(),
                        Type = messageType.Name,
                        CountOfRetry = maxRetryInfo.countOfRetry,
                        CreatedAt_EnglishDate = nowDateTime,
                        CreatedAt_PersianDate = _dateTime.ToPersianShortDate(nowDateTime)
                    };

                    consumerEventQueryRepository.Add(consumerEventQuery);

                    #endregion
                
                    await (Task)messageBusHandlerMethod.Invoke(messageBusHandler, new[] { message, cancellationToken });
                
                    await unitOfWork.CommitAsync(cancellationToken);

                    _CleanCache(messageBusHandlerMethod, serviceProvider);
                }
            }
            
            _TrySendAckMessage(channel, args);
        }
        catch (Exception e)
        {
            e.FileLogger(_hostEnvironment, _dateTime);
            
            e.ElasticStackExceptionLogger(_hostEnvironment, _globalUniqueIdGenerator, _dateTime, 
                NameOfService, NameOfAction
            );
            
            if(unitOfWork is not null) await unitOfWork.RollbackAsync(cancellationToken);

            _RequeueMessageAsDeadLetter(channel, args);
        }
    }
    
    private void _EventOfQueueHandle(IModel channel, BasicDeliverEventArgs args, Event @event, string service,
        IServiceProvider serviceProvider
    )
    {
        IUnitOfWork unitOfWork   = null;
        Type eventBusHandlerType = null;

        try
        {
            var useCaseTypes = Assembly.Load(new AssemblyName("Domic.UseCase")).GetTypes();

            var targetConsumerEventBusHandlerType = useCaseTypes.FirstOrDefault(
                type => type.GetInterfaces().Any(
                    i => i.IsGenericType &&
                         i.GetGenericTypeDefinition() == typeof(IConsumerEventBusHandler<>) &&
                         i.GetGenericArguments().Any(arg => arg.Name.Equals(@event.Type))
                )
            );

            if (targetConsumerEventBusHandlerType is not null)
            {
                var eventType = targetConsumerEventBusHandlerType.GetInterfaces()
                                                                 .Select(i => i.GetGenericArguments()[0])
                                                                 .FirstOrDefault();
        
                var fullContractOfConsumerType = typeof(IConsumerEventBusHandler<>).MakeGenericType(eventType);

                var eventBusHandler = serviceProvider.GetRequiredService(fullContractOfConsumerType);
                
                eventBusHandlerType = eventBusHandler.GetType();
                
                var payload = JsonConvert.DeserializeObject(@event.Payload, eventType);

                var eventBusHandlerMethod =
                    eventBusHandlerType.GetMethod("Handle") ?? throw new Exception("Handle function not found !");

                var retryAttr =
                    eventBusHandlerMethod.GetCustomAttribute(typeof(WithMaxRetryAttribute)) as WithMaxRetryAttribute;

                var maxRetryInfo = _IsMaxRetryMessage(args, retryAttr);
                
                if (maxRetryInfo.result)
                {
                    if (retryAttr.HasAfterMaxRetryHandle)
                    {
                        var afterMaxRetryHandlerMethod =
                            eventBusHandlerType.GetMethod("AfterMaxRetryHandle") ?? throw new Exception("AfterMaxRetryHandle function not found !");
                        
                        afterMaxRetryHandlerMethod.Invoke(eventBusHandler, new[] { payload });
                    }
                }
                else
                {
                    var transactionConfig =
                        eventBusHandlerMethod.GetCustomAttribute(typeof(TransactionConfigAttribute)) as TransactionConfigAttribute;

                    //for query side event processing
                    if (transactionConfig.Type == TransactionType.Query)
                    {
                        var consumerEventQueryRepository =
                            serviceProvider.GetRequiredService<IConsumerEventQueryRepository>();
                        
                        var consumerEventQuery = consumerEventQueryRepository.FindById(@event.Id);
                        
                        if (consumerEventQuery is null)
                        {
                            unitOfWork =
                                serviceProvider.GetRequiredService(_GetTypeOfUnitOfWork(TransactionType.Query)) as IUnitOfWork;

                            unitOfWork.Transaction(transactionConfig.IsolationLevel);

                            #region IdempotentConsumerPattern

                            var nowDateTime = DateTime.Now;
                        
                            consumerEventQuery = new ConsumerEventQuery {
                                Id = @event.Id,
                                Type = @event.Type,
                                CountOfRetry = maxRetryInfo.countOfRetry,
                                CreatedAt_EnglishDate = nowDateTime,
                                CreatedAt_PersianDate = _dateTime.ToPersianShortDate(nowDateTime)
                            };
                                
                            consumerEventQueryRepository.Add(consumerEventQuery);

                            #endregion
            
                            eventBusHandlerMethod.Invoke(eventBusHandler, new[] { payload });

                            unitOfWork.Commit();
        
                            _CleanCache(eventBusHandlerMethod, serviceProvider);
                        }
                    }
                    //for command side event processing
                    else if (transactionConfig.Type == TransactionType.Command)
                    {
                        var consumerEventCommandRepository =
                            serviceProvider.GetRequiredService<IConsumerEventCommandRepository>();

                        var consumerEventCommand = consumerEventCommandRepository.FindById(@event.Id);

                        if (consumerEventCommand is null)
                        {
                            unitOfWork =
                                serviceProvider.GetRequiredService(_GetTypeOfUnitOfWork(TransactionType.Command)) as IUnitOfWork;

                            unitOfWork.Transaction(transactionConfig.IsolationLevel);

                            #region IdempotentConsumerPattern

                            var nowDateTime = DateTime.Now;

                            consumerEventCommand = new ConsumerEvent {
                                Id = @event.Id,
                                Type = @event.Type,
                                CountOfRetry = maxRetryInfo.countOfRetry,
                                CreatedAt_EnglishDate = nowDateTime,
                                CreatedAt_PersianDate = _dateTime.ToPersianShortDate(nowDateTime)
                            };

                            consumerEventCommandRepository.Add(consumerEventCommand);

                            #endregion

                            eventBusHandlerMethod.Invoke(eventBusHandler, new[] { payload });

                            unitOfWork.Commit();

                            _CleanCache(eventBusHandlerMethod, serviceProvider);
                        }
                    }
                    else 
                        throw new Exception("Must be defined transaction type!");
                }
            }
            
            _TrySendAckMessage(channel, args);
        }
        catch (Exception e)
        {
            e.FileLogger(_hostEnvironment, _dateTime);
            
            e.ElasticStackExceptionLogger(_hostEnvironment, _globalUniqueIdGenerator, _dateTime, 
                NameOfService, NameOfAction
            );
            
            e.CentralExceptionLogger(_hostEnvironment, _globalUniqueIdGenerator, this, _dateTime, service, 
                eventBusHandlerType is not null ? eventBusHandlerType.Name : NameOfAction
            );

            unitOfWork?.Rollback();

            _RequeueMessageAsDeadLetter(channel, args);
        }
    }
    
    private async Task _EventOfQueueHandleAsync(IModel channel, BasicDeliverEventArgs args, Event @event, 
        string service, IServiceProvider serviceProvider, CancellationToken cancellationToken
    )
    {
        Type eventBusHandlerType = null;
        IUnitOfWork unitOfWork = null;

        try
        {
            var useCaseTypes = Assembly.Load(new AssemblyName("Domic.UseCase")).GetTypes();

            var targetConsumerEventBusHandlerType = useCaseTypes.FirstOrDefault(
                type => type.GetInterfaces().Any(
                    i => i.IsGenericType &&
                         i.GetGenericTypeDefinition() == typeof(IConsumerEventBusHandler<>) &&
                         i.GetGenericArguments().Any(arg => arg.Name.Equals(@event.Type))
                )
            );

            if (targetConsumerEventBusHandlerType is not null)
            {
                var eventType = targetConsumerEventBusHandlerType.GetInterfaces()
                                                                 .Select(i => i.GetGenericArguments()[0])
                                                                 .FirstOrDefault();
        
                var fullContractOfConsumerType = typeof(IConsumerEventBusHandler<>).MakeGenericType(eventType);
            
                var eventBusHandler = serviceProvider.GetRequiredService(fullContractOfConsumerType);
                
                eventBusHandlerType = eventBusHandler.GetType();
                
                var payload = JsonConvert.DeserializeObject(@event.Payload, eventType);

                var eventBusHandlerMethod =
                    eventBusHandlerType.GetMethod("HandleAsync") ?? throw new Exception("HandleAsync function not found !");

                var retryAttr =
                    eventBusHandlerMethod.GetCustomAttribute(typeof(WithMaxRetryAttribute)) as WithMaxRetryAttribute;

                var maxRetryInfo = _IsMaxRetryMessage(args, retryAttr);
                
                if (maxRetryInfo.result)
                {
                    if (retryAttr.HasAfterMaxRetryHandle)
                    {
                        var afterMaxRetryHandlerMethod =
                            eventBusHandlerType.GetMethod("AfterMaxRetryHandleAsync") ?? throw new Exception("AfterMaxRetryHandleAsync function not found !");
                        
                        await (Task)afterMaxRetryHandlerMethod.Invoke(eventBusHandler, new[] { payload, cancellationToken });
                    }
                }
                else
                {
                    var transactionConfig =
                        eventBusHandlerMethod.GetCustomAttribute(typeof(TransactionConfigAttribute)) as TransactionConfigAttribute;

                    //for query side event processing
                    if (transactionConfig.Type == TransactionType.Query)
                    {
                        var consumerEventQueryRepository =
                            serviceProvider.GetRequiredService<IConsumerEventQueryRepository>();
                        
                        var consumerEventQuery =
                            await consumerEventQueryRepository.FindByIdAsync(@event.Id, cancellationToken);
                        
                        if (consumerEventQuery is null)
                        {
                            unitOfWork =
                                serviceProvider.GetRequiredService(_GetTypeOfUnitOfWork(TransactionType.Query)) as IUnitOfWork;

                            await unitOfWork.TransactionAsync(transactionConfig.IsolationLevel, cancellationToken);

                            #region IdempotentConsumerPattern

                            var nowDateTime = DateTime.Now;
                        
                            consumerEventQuery = new ConsumerEventQuery {
                                Id = @event.Id,
                                Type = @event.Type,
                                CountOfRetry = maxRetryInfo.countOfRetry,
                                CreatedAt_EnglishDate = nowDateTime,
                                CreatedAt_PersianDate = _dateTime.ToPersianShortDate(nowDateTime)
                            };
                                
                            consumerEventQueryRepository.Add(consumerEventQuery);

                            #endregion
            
                            await (Task)eventBusHandlerMethod.Invoke(eventBusHandler, new[] { payload, cancellationToken });

                            await unitOfWork.CommitAsync(cancellationToken);
        
                            _CleanCache(eventBusHandlerMethod, serviceProvider);
                        }
                    }
                    //for command side event processing
                    else if (transactionConfig.Type == TransactionType.Command)
                    {
                        var consumerEventCommandRepository =
                            serviceProvider.GetRequiredService<IConsumerEventCommandRepository>();

                        var consumerEventCommand =
                            await consumerEventCommandRepository.FindByIdAsync(@event.Id, cancellationToken);

                        if (consumerEventCommand is null)
                        {
                            unitOfWork =
                                serviceProvider.GetRequiredService(_GetTypeOfUnitOfWork(TransactionType.Command)) as IUnitOfWork;

                            await unitOfWork.TransactionAsync(transactionConfig.IsolationLevel, cancellationToken);

                            #region IdempotentConsumerPattern

                            var nowDateTime = DateTime.Now;

                            consumerEventCommand = new ConsumerEvent {
                                Id = @event.Id,
                                Type = @event.Type,
                                CountOfRetry = maxRetryInfo.countOfRetry,
                                CreatedAt_EnglishDate = nowDateTime,
                                CreatedAt_PersianDate = _dateTime.ToPersianShortDate(nowDateTime)
                            };

                            consumerEventCommandRepository.Add(consumerEventCommand);

                            #endregion

                            await (Task)eventBusHandlerMethod.Invoke(eventBusHandler, new[] { payload, cancellationToken });

                            await unitOfWork.CommitAsync(cancellationToken);

                            _CleanCache(eventBusHandlerMethod, serviceProvider);
                        }
                    }
                    else throw new Exception("Must be defined transaction type!");
                }
            }
            
            _TrySendAckMessage(channel, args);
        }
        catch (Exception e)
        {
            e.FileLogger(_hostEnvironment, _dateTime);
            
            e.ElasticStackExceptionLogger(_hostEnvironment, _globalUniqueIdGenerator, _dateTime, 
                NameOfService, NameOfAction
            );
            
            e.CentralExceptionLogger(_hostEnvironment, _globalUniqueIdGenerator, this, _dateTime, service, 
                eventBusHandlerType is not null ? eventBusHandlerType.Name : NameOfAction
            );

            if (unitOfWork is not null) await unitOfWork.RollbackAsync(cancellationToken);

            _RequeueMessageAsDeadLetter(channel, args);
        }
    }
    
    private void _RequeueMessageAsDeadLetter(IModel channel, BasicDeliverEventArgs args)
    {
        try
        {
            channel.BasicNack(args.DeliveryTag, false, false); //or _channel.BasicReject(args.DeliveryTag, false);
        }
        catch (Exception e)
        {
            e.FileLogger(_hostEnvironment, _dateTime);
            
            e.ElasticStackExceptionLogger(_hostEnvironment, _globalUniqueIdGenerator, _dateTime, 
                NameOfService, NameOfAction
            );
        }
    }
    
    private (bool result, int countOfRetry) _IsMaxRetryMessage(BasicDeliverEventArgs args, WithMaxRetryAttribute maxRetryAttribute)
    {
        var xDeath = args.BasicProperties.Headers?.FirstOrDefault(header => header.Key.Equals("x-death")).Value;

        var xDeathInfo = (xDeath as List<object>)?.FirstOrDefault() as Dictionary<string, object>;
                
        var countRetry = xDeathInfo?.FirstOrDefault(header => header.Key.Equals("count")).Value;

        return ( Convert.ToInt32(countRetry) > maxRetryAttribute?.Count , Convert.ToInt32(countRetry) );
    }
    
    private void _TrySendAckMessage(IModel channel, BasicDeliverEventArgs args)
    {
        try
        {
            channel.BasicAck(args.DeliveryTag, false); //delete this message from queue
        }
        catch (Exception e)
        {
            e.FileLogger(_hostEnvironment, _dateTime);
            
            e.ElasticStackExceptionLogger(_hostEnvironment, _globalUniqueIdGenerator, _dateTime, 
                NameOfService, NameOfAction
            );
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
    
    private Type _GetTypeOfCommandUnitOfWork()
    {
        var domainTypes = Assembly.Load(new AssemblyName("Domic.Domain")).GetTypes();

        return domainTypes.FirstOrDefault(
            type => type.GetInterfaces().Any(i => i == typeof(ICoreCommandUnitOfWork))
        );
    }
    
    private void _CleanCache(MethodInfo eventBusHandlerMethod, IServiceProvider serviceProvider)
    {
        try
        {
            if (eventBusHandlerMethod.GetCustomAttribute(typeof(WithCleanCacheAttribute)) is WithCleanCacheAttribute withCleanCacheAttribute)
            {
                var redisCache = serviceProvider.GetRequiredService<IInternalDistributedCache>();

                foreach (var key in withCleanCacheAttribute.Keies.Split("|"))
                    redisCache.DeleteKey(key);
            }
        }
        catch (Exception e)
        {
            e.FileLogger(_hostEnvironment, _dateTime);
            
            e.ElasticStackExceptionLogger(_hostEnvironment, _globalUniqueIdGenerator, _dateTime, 
                NameOfService, NameOfAction
            );
        }
    }
}