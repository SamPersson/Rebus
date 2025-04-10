﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Rebus.Bus;
using Rebus.Config;
using Rebus.Exceptions;
using Rebus.Logging;
using Rebus.Messages;
using Rebus.Pipeline;
using Rebus.Pipeline.Receive;
using Rebus.Transport;
// ReSharper disable ArgumentsStyleLiteral

namespace Rebus.Sagas;

/// <summary>
/// Incoming step that loads and saves relevant saga data.
/// </summary>
[StepDocumentation(@"Looks at the handler invokers in the context and sees if there's one or more saga handlers in there. 

If that's the case, relevant saga data is loaded/created, and the rest of the pipeline gets invoked.

Afterwards, all the created/loaded saga data is updated appropriately.")]
public class LoadSagaDataStep : IIncomingStep
{
    const string RevisionPropertyName = nameof(ISagaData.Revision);

    /// <summary>
    /// properties ignored by auto-setter (the one that automatically sets the correlation ID on a new saga data instance)
    /// </summary>
    static readonly string[] IgnoredProperties =
    {
        RevisionPropertyName
    };

    readonly ICorrelationErrorHandler _correlationErrorHandler;
    readonly ISagaDataIdFactory _sagaDataIdFactory;
    readonly SagaHelper _sagaHelper = new();
    readonly ISagaStorage _sagaStorage;
    readonly Options _options;
    readonly ILog _log;

    /// <summary>
    /// Constructs the step with the given saga storage
    /// </summary>
    public LoadSagaDataStep(ISagaStorage sagaStorage, ICorrelationErrorHandler correlationErrorHandler, ISagaDataIdFactory sagaDataIdFactory, IRebusLoggerFactory rebusLoggerFactory, Options options)
    {
        if (rebusLoggerFactory == null) throw new ArgumentNullException(nameof(rebusLoggerFactory));
        _sagaStorage = sagaStorage ?? throw new ArgumentNullException(nameof(sagaStorage));
        _correlationErrorHandler = correlationErrorHandler ?? throw new ArgumentNullException(nameof(correlationErrorHandler));
        _sagaDataIdFactory = sagaDataIdFactory ?? throw new ArgumentNullException(nameof(sagaDataIdFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = rebusLoggerFactory.GetLogger<LoadSagaDataStep>();
    }

    /// <summary>
    /// For each <see cref="HandlerInvoker"/> found in the current <see cref="IncomingStepContext"/>'s <see cref="HandlerInvokers"/>,
    /// this step will see if the invoker's handler is actually a <see cref="Saga"/>. If that is the case, the saga's correlation properties
    /// are used to see if a piece of existing saga data can be retrieved and mounted on the <see cref="Saga{TSagaData}.Data"/> property.
    /// If no existing instance was found, but the saga implements <see cref="IAmInitiatedBy{TMessage}"/> for the current message,
    /// a new saga data instance will be created (and mounted). Otherwise, the message is ignored.
    /// </summary>
    public async Task Process(IncomingStepContext context, Func<Task> next)
    {
        // first we get the relevant handler invokers
        var handlerInvokersForSagas = context.Load<HandlerInvokers>()
            .Where(l => l.HasSaga)
            .ToList();

        // maybe short-circuit? this makes it slightly faster
        if (!handlerInvokersForSagas.Any())
        {
            await next();
            return;
        }

        var message = context.Load<Message>();
        var label = message.GetMessageLabel();
        var transactionContext = context.Load<ITransactionContext>();

        // keep track of saga data instances in these two lists
        var loadedSagaData = new List<RelevantSagaInfo>();
        var newlyCreatedSagaData = new List<RelevantSagaInfo>();

        // and then we process them
        foreach (var sagaInvoker in handlerInvokersForSagas)
        {
            await TryMountSagaDataOnInvoker(sagaInvoker, message, label, loadedSagaData, newlyCreatedSagaData, transactionContext);
        }

        // invoke the rest of the pipeline (most likely also dispatching the incoming message to the now-ready saga handlers)
        await next();

        // everything went well - let's divide saga data instances into those to insert, update, and delete
        var newlyCreatedSagaDataToSave = newlyCreatedSagaData.Where(s => !s.Saga.WasMarkedAsComplete && !s.Saga.WasMarkedAsUnchanged);
        var loadedSagaDataToUpdate = loadedSagaData.Where(s => !s.Saga.WasMarkedAsComplete && !s.Saga.WasMarkedAsUnchanged);
        var loadedSagaDataToDelete = loadedSagaData.Where(s => s.Saga.WasMarkedAsComplete);

        foreach (var sagaDataToInsert in newlyCreatedSagaDataToSave)
        {
            await SaveSagaData(sagaDataToInsert, insert: true);
        }

        foreach (var sagaDataToUpdate in loadedSagaDataToUpdate)
        {
            await SaveSagaData(sagaDataToUpdate, insert: false);
        }

        foreach (var sagaDataToUpdate in loadedSagaDataToDelete)
        {
            await _sagaStorage.Delete(sagaDataToUpdate.SagaData);
        }
    }

    async Task TryMountSagaDataOnInvoker(HandlerInvoker sagaInvoker, Message message, string label, List<RelevantSagaInfo> loadedSagaData, List<RelevantSagaInfo> newlyCreatedSagaData, ITransactionContext transactionContext)
    {
        var foundExistingSagaData = false;

        var body = message.Body;
        var correlationProperties = _sagaHelper.GetCorrelationProperties(sagaInvoker.Saga);
        var correlationPropertiesRelevantForMessage = correlationProperties.ForMessage(body).ToArray();

        Type sagaDataType = sagaInvoker.Saga.GetSagaDataType();
        foreach (var correlationProperty in correlationPropertiesRelevantForMessage)
        {
            var valueFromMessage = correlationProperty.GetValueFromMessage(new MessageContext(transactionContext), message);
            var sagaData = await _sagaStorage.Find(sagaDataType, correlationProperty.PropertyName, valueFromMessage);

            if (sagaData == null) continue;

            sagaInvoker.SetSagaData(sagaData);
            foundExistingSagaData = true;
            loadedSagaData.Add(new RelevantSagaInfo(sagaData, correlationProperties, sagaInvoker.Saga));

            _log.Debug("Found existing saga data with ID {sagaDataId} for message {messageLabel}", sagaData.Id, label);
            break;
        }

        if (!foundExistingSagaData)
        {
            var messageType = body.GetType();
            var canBeInitiatedByThisMessageType = sagaInvoker.CanBeInitiatedBy(messageType);

            if (canBeInitiatedByThisMessageType)
            {
                var newSagaData = _sagaHelper.CreateNewSagaData(sagaInvoker.Saga, _sagaDataIdFactory);

                // if there's exacly one correlation property that points to a property on the saga data, we can set it
                if (correlationPropertiesRelevantForMessage.Length == 1)
                {
                    TrySetCorrelationPropertyValue(newSagaData, correlationPropertiesRelevantForMessage[0], message, transactionContext);
                }

                sagaInvoker.SetSagaData(newSagaData);

                _log.Debug("Created new saga data with ID {sagaDataId} for message {messageLabel}", newSagaData.Id, label);

                newlyCreatedSagaData.Add(new RelevantSagaInfo(newSagaData, correlationProperties, sagaInvoker.Saga));
            }
            else
            {
                await _correlationErrorHandler.HandleCorrelationError(correlationProperties, sagaInvoker, message);
            }
        }
    }

    static void TrySetCorrelationPropertyValue(ISagaData newSagaData, CorrelationProperty correlationProperty, Message message, ITransactionContext transactionContext)
    {
        try
        {
            if (IgnoredProperties.Contains(correlationProperty.PropertyName)) return;

            var correlationPropertyInfo = newSagaData.GetType().GetProperty(correlationProperty.PropertyName);

            if (correlationPropertyInfo == null) return;

            var valueFromMessage = correlationProperty.GetValueFromMessage(new MessageContext(transactionContext), message);

            correlationPropertyInfo.SetValue(newSagaData, valueFromMessage);
        }
        catch (Exception)
        {
            // if this fails it might be because the property is not settable.... just leave it to the programmer in the other end to set it
        }
    }

    async Task SaveSagaData(RelevantSagaInfo sagaDataToUpdate, bool insert)
    {
        var sagaData = sagaDataToUpdate.SagaData;
        var sagaDataId = sagaData.Id;
        var sagaDataType = sagaData.GetType();
        var saga = sagaDataToUpdate.Saga;

        var saveAttempts = 0;

        while (true)
        {
            try
            {
                saveAttempts++;

                if (insert)
                {
                    await _sagaStorage.Insert(sagaData, sagaDataToUpdate.CorrelationProperties);
                }
                else
                {
                    await _sagaStorage.Update(sagaData, sagaDataToUpdate.CorrelationProperties);
                }

                return;
            }
            catch (ConcurrencyException)
            {
                if (saveAttempts > _options.MaxConflictResolutionAttempts) throw;

                // if we get a concurrencyexception on insert, we would not be able to look up the saga by its ID, we would have to look it up by correlating.... could do that too, but then it would swap the order of saga data instances when resolving the conflict
                if (insert) throw; //< ^^^ - therefore: disable conflict resolution on insert because: how?

                var userHasOverriddenConflictResolutionMethod = sagaDataToUpdate.Saga.UserHasOverriddenConflictResolutionMethod();

                if (!userHasOverriddenConflictResolutionMethod)
                {
                    throw;
                }

                var freshSagaData = await _sagaStorage.Find(sagaDataType, "Id", sagaDataId);

                if (freshSagaData == null)
                {
                    throw new RebusApplicationException($"Could not find {sagaDataType} saga data with ID {sagaDataId} when attempting to invoke conflict resolution - it must have been deleted");
                }

                _log.Debug("Invoking conflict resolution attempt {resolutionAttemps} for saga with ID {sagaDataId}", saveAttempts, sagaDataId);

                await saga.InvokeConflictResolution(freshSagaData);

                sagaData.Revision = freshSagaData.Revision;
            }
        }
    }

    sealed class RelevantSagaInfo
    {
        public RelevantSagaInfo(ISagaData sagaData, IEnumerable<CorrelationProperty> correlationProperties, Saga saga)
        {
            SagaData = sagaData;

            // only keep necessary correlation properties, i.e.
            CorrelationProperties = correlationProperties
                .GroupBy(p => p.PropertyName)
                .Select(g => g.First())
                .ToList();

            Saga = saga;
        }

        public ISagaData SagaData { get; }

        public List<CorrelationProperty> CorrelationProperties { get; }

        public Saga Saga { get; }
    }
}