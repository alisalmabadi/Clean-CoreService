﻿#pragma warning disable CS8604

using System.Reflection;
using Domic.Core.UseCase.Commons.Attributes;
using Domic.Core.UseCase.Contracts.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Domic.Core.WebAPI.Jobs;

public class EventStreamConsumerJob : IHostedService
{
    private readonly IEventStreamBroker _eventStreamBroker;
    private readonly IConfiguration _configuration;

    public EventStreamConsumerJob(IEventStreamBroker eventStreamBroker, IConfiguration configuration)
    {
        _eventStreamBroker = eventStreamBroker;
        _configuration = configuration;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _eventStreamBroker.NameOfAction  = nameof(EventStreamConsumerJob);
        _eventStreamBroker.NameOfService = _configuration.GetValue<string>("NameOfService");
        
        var useCaseTypes = Assembly.Load(new AssemblyName("Domic.UseCase")).GetTypes();
        
        var eventStreamHandlerTypes = useCaseTypes.Where(type =>
            type.GetInterfaces().Any(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConsumerEventStreamHandler<>)
            )
        );

        var topics = eventStreamHandlerTypes.Select(type => 
            ( type.GetCustomAttribute(typeof(StreamConsumerAttribute)) as StreamConsumerAttribute )?.Topic
        );

        foreach (var topic in topics)
            if(topic is not null)
                if (_configuration.GetValue<bool>("IsExternalBrokerConsumingAsync"))
                    _LongRunningListenerAsNonBlockingAndAsynchronously(topic, cancellationToken);
                else
                    _LongRunningListenerAsNonBlockingAndSequential(topic, cancellationToken);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    
    /*---------------------------------------------------------------*/
    
    private void _LongRunningListenerAsNonBlockingAndSequential(string topic, CancellationToken cancellationToken)
    {
        Task.Factory.StartNew(() => _eventStreamBroker.Subscribe(topic, cancellationToken),
            TaskCreationOptions.LongRunning
        );
    }
    
    private void _LongRunningListenerAsNonBlockingAndAsynchronously(string topic, CancellationToken cancellationToken)
    {
        Task.Factory.StartNew(() => _eventStreamBroker.SubscribeAsynchronously(topic, cancellationToken),
            TaskCreationOptions.LongRunning
        );
    }
}