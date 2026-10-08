namespace Nexa.Xsr.Runtime.Tests;

internal static partial class Program
{
    private static readonly (string Name, Func<ValueTask> Body)[] TestCases =
    [
        ("Host facade preserves admission cancellation and shutdown errors", HostFacadePreservesAdmissionCancellationAndShutdownErrors),
        ("Host facade returns stable catalog budget failure", HostFacadeReloadReturnsStableFailureForCatalogBudget),
        ("initial snapshot preserves store and owns candidate values", Sync(CoreInitialSnapshotPreservesStoreAndOwnsCandidateValues)),
        ("initial snapshot rejects invalid candidates without publication", Sync(CoreInitialSnapshotRejectsInvalidCandidatesWithoutPublication)),
        ("initial snapshot races writers and readers without lost writes", CoreInitialSnapshotRacesWritersAndReadersWithoutLosingWrites),
        ("physical Sidecar Host API completes lifecycle", PhysicalSidecarHostApiCompletesLifecycle),
        ("physical Sidecar recovery is bounded and isolated", PhysicalSidecarRecoveryIsBoundedAndIsolated),
        ("physical Sidecar reload reverifies admitted packages", PhysicalSidecarReloadReverifiesAdmittedPackages),
        ("Sidecar disposal cancels unpublished startup", SupervisorDisposalCancelsUnpublishedStartup),
        ("Sidecar handshake owns deadline and terminal cleanup", SessionHandshakeOwnsDeadlineAndTerminalCleanup),
        ("Sidecar lifecycle serializes initial reader phases", SessionLifecycleSerializesInitialReaderPhases),
        ("Sidecar registration requires negotiated binary stream features", SessionRegistrationRequiresNegotiatedBinaryAndStreamFeatures),
        ("Sidecar initial snapshot publishes one coherent store", SessionInitialSnapshotPublishesOneCoherentStore),
        ("Sidecar deactivation pauses data and supports reactivation", SessionDeactivationPausesDataPlaneAndSupportsReactivation),
        ("Sidecar graceful shutdown preserves ordered wire controls", SessionGracefulShutdownPreservesOrderedWireControls),
        ("Sidecar shutdown without unregister reply still sends shutdown", SessionShutdownWithoutUnregisterReplyStillSendsShutdown),
        ("Sidecar explicit and peer unregister retire session", SessionExplicitAndPeerUnregistrationRetireTheSession),
        ("Sidecar binary data API uses numeric contracts", SidecarBinaryDataApiUsesDeclaredNumericContracts),
        ("Sidecar data API rejects unavailable features and pre-cancel locally", SidecarDataApiRejectsUnavailableFeaturesAndPreCancellationLocally),
        ("Sidecar data API binds response message and codec", SidecarDataApiBindsResultMessageAndCodec),
        ("Sidecar data API ignores malformed late results and normalizes errors", SidecarDataApiIgnoresMalformedLateResultsAndNormalizesErrors),
        ("Sidecar health correlates nonce and ignores late pong", SidecarHealthApiCorrelatesNonceAndIgnoresLatePong),
        ("Sidecar health times out and stops on deactivation", SidecarHealthApiTimesOutAndStopsPendingOnDeactivation),
        ("Sidecar health rejects mismatched nonce and retires all checks", SidecarHealthApiRejectsMismatchedNonceAndRetiresAllChecks),
        ("Sidecar remote error completes only correlated exchange", SidecarRemoteErrorCompletesOnlyItsCorrelatedExchange),
        ("legacy Sidecar rejects binary codec zero representation", SidecarLegacySessionRejectsBinaryCodecZeroRepresentation),
        ("Sidecar streams return credit and drain in order", SidecarStreamsReturnCreditAndDrainInOrder),
        ("Sidecar streams finish before open flush completes", SidecarStreamsCanFinishBeforeTheOpenFlushCompletes),
        ("Sidecar completed streams retain unread chunks after retirement", SidecarCompletedStreamsPreserveUnreadChunksAcrossSessionRetirement),
        ("Sidecar streams reject sequence codec and credit violations", SidecarStreamsRejectSequenceCodecAndCreditViolations),
        ("Sidecar streams bound bytes and count", SidecarStreamsBoundSessionBytesAndStreamCount),
        ("Sidecar streams cancel timeout and enforce one consumer", SidecarStreamsCancelTimeoutAndEnforceOneConsumer),
        ("sidecar registration tables own and index declarations", Sync(SidecarRegistrationTablesOwnAndIndexDeclarations)),
        ("sidecar registration tables reject malformed declarations", Sync(SidecarRegistrationTablesRejectMalformedDeclarations)),
        ("sidecar value codecs reject malformed values and own bytes", Sync(SidecarRuntimeCodecsRejectMalformedValuesAndOwnBytes)),
        ("sidecar verified caches own public arrays", Sync(SidecarVerifiedCacheOwnsAllPublicArrays)),
        ("sidecar registered content reads locally without aliases", SidecarRegisteredContentAndExtensionsReadLocallyWithoutAliases),
        ("modules publish immutable state and restore live predecessor", ModulesPublishImmutableStateAndRestoreLivePredecessor),
        ("modules reject unauthorized malformed and over budget batches", ModulesRejectUnauthorizedMalformedAndOverBudgetBatches),
        ("module publications remain ordered under reentrancy and blocked observers", ModulePublicationsRemainOrderedUnderReentrancyAndBlockedObservers),
        ("module terminal paths retire and buffered activation cannot resurrect", ModuleTerminalPathsRetireAndBufferedActivationCannotResurrect),
        ("module activation failure does not strand earlier caption publication", ModuleActivationFailureDoesNotStrandEarlierCaptionPublication),
        ("caption patches publish immutable state and restore prior activations", CaptionPatchesPublishImmutableStateAndRestorePriorActivations),
        ("caption terminal paths restore and buffered activation cannot resurrect", CaptionTerminalPathsRestoreAndBufferedActivationCannotResurrect),
        ("caption patches reject grants budgets and malformed payloads atomically", CaptionPatchesRejectGrantsBudgetsAndMalformedPayloadsAtomically),
        ("caption publications cannot regress when observer blocks or disposes session", CaptionPublicationsCannotRegressWhenObserverBlocksOrDisposesSession),
        ("signals execute locally and notify in order", SignalsExecuteLocallyAndNotifyInOrder),
        ("signal budgets are atomic and concurrent wait delivers once", SignalBudgetsAreAtomicAndConcurrentWaitDeliversOnce),
        ("signals reject unauthorized or malformed registration atomically", SignalsRejectUnauthorizedOrMalformedRegistrationAtomically),
        ("signal overflow disables catch and fails without blocking caller", SignalOverflowDisablesCatchAndFailsSessionWithoutBlockingCaller),
        ("signal terminal paths retire and buffered activation cannot resurrect", SignalTerminalPathsRetireAndBufferedActivationCannotResurrect),
        ("inactive signals allocate nothing and wire rejects missing identity", Sync(InactiveSignalsAllocateNothingAndWireRejectsMissingIdentity)),
        ("sidecar shutdown tolerates an already closed transport", SidecarShutdownIsSafeAfterTransportClosure),
        ("extension registration commits atomically and retires with session", ExtensionsCommitAtomicallyAndRetireWithSession),
        ("renamed executable connects and registers extensions", RenamedExecutableConnectsAndRegistersExtensions),
        ("sidecar disposal cannot be undone by buffered snapshot", SidecarDisposalCannotBeUndoneByBufferedSnapshot),
        ("sidecar unsent cancellation preserves other requests", SidecarUnsentCancellationPreservesOtherRequests),
        ("sidecar deadlines include blocked writes", SidecarDeadlinesIncludeBlockedWrites),
        ("sidecar terminal paths complete all pending", SidecarTerminalPathsCompleteAllPending),
        ("sidecar concurrent admission respects capacity", SidecarConcurrentAdmissionRespectsCapacity),
        ("sidecar registration rejects unbounded input", SidecarRegistrationRejectsUnboundedInput),
        ("sidecar snapshot has independent budget", SidecarSnapshotHasIndependentBudget),
        ("semantic identifiers validate without normalization", Sync(SemanticIdentifiersValidateWithoutNormalization)),
        ("function patches execute five phases locally", FunctionPatchesExecuteFivePhasesLocally),
        ("function patches short circuit preserve exceptions and reentry", FunctionPatchesShortCircuitPreserveExceptionsAndReentry),
        ("function patch faults rollback and reset on reactivation", FunctionPatchFaultsRollbackAndResetOnReactivation),
        ("function patch snapshot survives concurrent retirement", FunctionPatchSnapshotSurvivesConcurrentRetirement),
        ("function patch validation rejects malformed programs and grants", FunctionPatchValidationRejectsMalformedProgramsAndGrants),
        ("function patch budgets are shared and activation is atomic", FunctionPatchBudgetsAreSharedAndActivationIsAtomic),
        ("function patch terminal paths retire programs", FunctionPatchTerminalPathsRetirePrograms),
        ("function patch no change hot path reuses buffers", FunctionPatchNoChangeHotPathReusesBuffers),
        ("function patch buffered activation cannot resurrect after disposal", FunctionPatchBufferedActivationCannotResurrectAfterDisposal),
        ("primitive function ABI executes typed phases and rollback", PrimitiveFunctionAbiExecutesTypedPhasesAndRollsBackOverflow),
        ("primitive function ABI rejects shapes and retired return", PrimitiveFunctionAbiRejectsInvalidShapesAndRetiresBeforeReturn),
        ("interactive UI binds numeric commands and live state", InteractiveDocumentsBindNumericCommandsAndLiveState),
        ("interactive UI rejects cross session bindings and images", InteractiveDocumentsRejectCrossSessionBindingsAndBadImages),
        ("interactive UI verified images obey owned byte and pixel budgets", InteractiveImagesUseOwnedVerifiedResourcesAndPixelBudgets),
        ("interactive UI replacement cancels and revokes identity", InteractiveDocumentReplacementCancelsPendingAndRevokesOldIdentity),
        ("runtime identifier zero remains reserved", Sync(RuntimeIdentifierZeroRemainsReserved)),
        ("registry assigns deterministic runtime identifiers", Sync(RegistryAssignsDeterministicRuntimeIdentifiers)),
        ("registry rejects duplicate semantic identifiers", Sync(RegistryRejectsDuplicateSemanticIdentifiers)),
        ("registry accepts concurrent unique registrations", Sync(RegistryAcceptsConcurrentUniqueRegistrations)),
        ("registry becomes immutable after sealing", Sync(RegistryBecomesImmutableAfterSealing)),
        ("snapshot resolves both identifier directions", Sync(SnapshotResolvesBothIdentifierDirections)),
        ("snapshot supports concurrent numeric reads", Sync(SnapshotSupportsConcurrentNumericReads)),
        ("snapshot numeric reads allocate no managed memory", Sync(SnapshotNumericReadsAllocateNoManagedMemory)),
        ("routers assign deterministic typed identifiers", RoutersAssignDeterministicTypedIdentifiers),
        ("command acceptance is separate from completion", CommandAcceptanceIsSeparateFromCompletion),
        ("detached command failures remain observable", DetachedCommandFailuresRemainObservable),
        ("command cancellation has a stable error", CommandCancellationHasAStableError),
        ("query results and timeouts remain distinct", QueryResultsAndTimeoutsRemainDistinct),
        ("query contract mismatches have a stable error", QueryContractMismatchesHaveAStableError),
        ("handler exceptions do not escape routing", HandlerExceptionsDoNotEscapeRouting),
        ("command dispatch supports concurrent callers", CommandDispatchSupportsConcurrentCallers),
        ("state assigns deterministic identifiers and ownership", Sync(StateAssignsDeterministicIdentifiersAndOwnership)),
        ("state cells start unavailable until published", Sync(StateCellsStartUnavailableUntilPublished)),
        ("state revisions are monotonic per entry", Sync(StateRevisionsAreMonotonicPerEntry)),
        ("state reads see coherent snapshots", Sync(StateReadsSeeCoherentSnapshots)),
        ("state coalescing applies latest and counts replaced", Sync(StateCoalescingAppliesLatestAndCountsReplaced)),
        ("state collection deltas apply against matching base", Sync(StateCollectionDeltasApplyAgainstMatchingBase)),
        ("collection sparse merge matches stable normalization", Sync(CollectionSparseMergeMatchesStableNormalization)),
        ("collection sparse merge bounds comparisons and preserves snapshots", Sync(CollectionSparseMergeBoundsComparisonsAndPreservesSnapshots)),
        ("collection failed merge leaves revision and notifications unchanged", Sync(CollectionFailedMergeLeavesRevisionAndNotificationsUnchanged)),
        ("state derived tracks late commits and availability", Sync(StateDerivedTracksLateCommitsAndAvailability)),
        ("state derived recomputes only when inputs change", Sync(StateDerivedRecomputesOnlyWhenInputsChange)),
        ("state derived chains propagate in order", Sync(StateDerivedChainsPropagateInOrder)),
        ("state derived uneven dependency revisions invalidate", Sync(StateDerivedUnevenDependencyRevisionsInvalidate)),
        ("state derived mutation during first compute does not commit stale value", StateDerivedMutationDuringFirstComputeDoesNotCommitStaleValue),
        ("state derived graph rejects cycles", Sync(StateDerivedGraphRejectsCycles)),
        ("state derived rejects undeclared dependency", Sync(StateDerivedRejectsUndeclaredDependency)),
        ("state availability is separate from value", Sync(StateAvailabilityIsSeparateFromValue)),
        ("state observers see ordered changes and cannot break publication", Sync(StateObserversSeeOrderedChangesAndCannotBreakPublication)),
        ("state contract mismatches are rejected", Sync(StateContractMismatchesAreRejected)),
        ("state builder rejects reuse and duplicates", Sync(StateBuilderRejectsReuseAndDuplicates)),
        ("state supports concurrent readers and publishers", Sync(StateSupportsConcurrentReadersAndPublishers)),
        // XSR-403: sidecar host session.
        ("session completes the locked lifecycle", SessionCompletesTheLockedLifecycle),
        ("session handshake rejects version mismatch", SessionHandshakeRejectsVersionMismatch),
        ("session registration rejects duplicates", SessionRegistrationRejectsDuplicates),
        ("session fails on unexpected message", SessionFailsOnUnexpectedMessage),
        // XSR-404: data plane, crash, and reconnect.
        ("command forwards and completes", CommandForwardsAndCompletes),
        ("command failure carries stable code", CommandFailureCarriesStableCode),
        ("query returns the sidecar value", QueryReturnsTheSidecarValue),
        ("command timeout returns stable error and releases pending", CommandTimeoutReturnsStableErrorAndReleasesPending),
        ("state deltas publish into the mirror", StateDeltasPublishIntoTheMirror),
        ("events deliver in order without coalescing", EventsDeliverInOrderWithoutCoalescing),
        ("crash fails session and marks mirror unavailable", CrashFailsSessionAndMarksMirrorUnavailable),
        ("stream failure marks session failed", StreamFailureMarksSessionFailed),
        ("reconnect replaces mirror with fresh snapshot", ReconnectReplacesMirrorWithFreshSnapshot),
        ("pending backpressure rejects with stable error", PendingBackpressureRejectsWithStableError),
        ("pending table stays bounded", PendingTableStaysBounded),
        ("unregistered commands reject locally without wire bytes", UnregisteredCommandsRejectLocally),
        // XSR-406: transactional snapshot, typed codecs, content registration.
        ("snapshot duplicate state rejected", SnapshotDuplicateStateRejected),
        ("snapshot missing state rejected", SnapshotMissingStateRejected),
        ("snapshot failure does not mutate mirror", SnapshotFailureDoesNotMutateMirror),
        ("snapshot commit is atomic", SnapshotCommitIsAtomic),
        ("typed bool state round trip", TypedBoolStateRoundTrip),
        ("typed int state round trip", TypedIntStateRoundTrip),
        ("generated dto round trip", GeneratedDtoRoundTrip),
        ("codec mismatch rejected", CodecMismatchRejected),
        ("ui module payload cached at registration", UiModulePayloadCachedAtRegistration),
        ("ui module open performs zero ipc", UiModuleOpenPerformsZeroIpc),
        ("resource hash deduplicates transfer", ResourceHashDeduplicatesTransfer),
        ("missing resource rejects ui module", MissingResourceRejectsUiModule),
        ("events assign deterministic typed identifiers", Sync(EventsAssignDeterministicTypedIdentifiers)),
        ("events share sequence space inside declared scope", Sync(EventsShareSequenceSpaceInsideDeclaredScope)),
        ("events order per scope key independently", Sync(EventsOrderPerScopeKeyIndependently)),
        ("events reject with backpressure instead of dropping", Sync(EventsRejectWithBackpressureInsteadOfDropping)),
        ("events evict freely without subscribers", Sync(EventsEvictFreelyWithoutSubscribers)),
        ("events replay retained records then continue live", Sync(EventsReplayRetainedRecordsThenContinueLive)),
        ("events cancellation returns stable error", EventsCancellationReturnsStableError),
        ("events reject contract mismatches and unknown routes", Sync(EventsRejectContractMismatchesAndUnknownRoutes)),
        ("events observe every publication without blocking it", Sync(EventsObserveEveryPublicationWithoutBlockingIt)),
        ("events preserve scope order under concurrent publishers", EventsPreserveScopeOrderUnderConcurrentPublishers),
        ("events reject undeclared scope and duplicate scope", Sync(EventsRejectUndeclaredScopeAndDuplicateScope)),
        ("scheduler runs work and observes completion", SchedulerRunsWorkAndObservesCompletion),
        ("scheduler cancellation is observed without running work", SchedulerCancellationIsObservedWithoutRunningWork),
        ("scheduler faults are classified and isolated", SchedulerFaultsAreClassifiedAndIsolated),
        ("scheduler dispose cancels pending work", Sync(SchedulerDisposeCancelsPendingWork)),
        ("scheduler work dispose then scheduler dispose is safe", Sync(SchedulerWorkDisposeThenSchedulerDisposeIsSafe)),
        ("scheduler zero delay stress", SchedulerZeroDelayStress),
        ("scope disposal releases and unregisters owned resources", Sync(ScopeDisposalReleasesAndUnregistersOwnedResources)),
        ("scope nesting disposes depth first", Sync(ScopeNestingDisposesDepthFirst)),
        ("scope bulk cleanup matches plugin unload", Sync(ScopeBulkCleanupMatchesPluginUnload)),
        ("lifecycle accepts only forward transitions", Sync(LifecycleAcceptsOnlyForwardTransitions)),
        ("lifecycle rejects illegal transitions", Sync(LifecycleRejectsIllegalTransitions)),
        ("lifecycle failure is terminal and observable", Sync(LifecycleFailureIsTerminalAndObservable)),
        ("lifecycle transitions are serialized under concurrency", Sync(LifecycleTransitionsAreSerializedUnderConcurrency)),
        ("lifecycle errors carry stable codes", Sync(LifecycleErrorsCarryStableCodes)),
        ("session trace is bounded and correlation addressable", Sync(SessionTraceIsBoundedAndCorrelationAddressable)),
        ("session trace correlates end to end across subsystems", SessionTraceCorrelatesEndToEndAcrossSubsystems),
    ];

    private static async Task<int> Main(string[] args)
    {
        if (args is ["--nexa-sidecar", "--endpoint", var endpoint])
            return File.Exists(Path.Combine(AppContext.BaseDirectory, "sidecar-full.fixture"))
                ? await RunCompleteSupervisorChildAsync(endpoint) : await RunSupervisorChildAsync(endpoint);
        int passed = 0;
        foreach ((string name, Func<ValueTask> body) in TestCases)
        {
            if (args.Contains("--skip-os-ipc") && name == "renamed executable connects and registers extensions")
            { Console.WriteLine("SKIP: OS IPC executable integration (explicit --skip-os-ipc)."); continue; }
            await body().ConfigureAwait(false);
            Console.WriteLine($"PASS: {name}");
            passed++;
        }

        Console.WriteLine($"XSR runtime tests passed: {passed}; skipped: {TestCases.Length - passed}.");
        return 0;
    }

    private static Func<ValueTask> Sync(Action action) => () =>
    {
        action();
        return ValueTask.CompletedTask;
    };

    private static void SemanticIdentifiersValidateWithoutNormalization()
    {
        XsrSemanticId identifier = XsrSemanticId.Parse("Minecraft.Launch_v2");

        AssertEqual("Minecraft.Launch_v2", identifier.Value);
        AssertTrue(identifier.IsAssigned);
        AssertFalse(XsrSemanticId.TryParse(null, out _));
        AssertFalse(XsrSemanticId.TryParse(string.Empty, out _));
        AssertFalse(XsrSemanticId.TryParse("minecraft launch", out _));
        AssertFalse(XsrSemanticId.TryParse("minecraft\u0000launch", out _));
        AssertThrows<ArgumentException>(() => XsrSemanticId.Parse(" minecraft.launch"));
    }

    private static void RuntimeIdentifierZeroRemainsReserved()
    {
        AssertFalse(default(XsrRuntimeId).IsAssigned);
        AssertThrows<ArgumentOutOfRangeException>(() => _ = new XsrRuntimeId(0));

        XsrRuntimeId identifier = new(42);
        AssertTrue(identifier.IsAssigned);
        AssertEqual(42u, identifier.Value);
    }

    private static void RegistryAssignsDeterministicRuntimeIdentifiers()
    {
        XsrSemanticId alpha = XsrSemanticId.Parse("alpha.command");
        XsrSemanticId zeta = XsrSemanticId.Parse("zeta.command");

        XsrRegistry<string> first = new();
        first.Register(zeta, "zeta");
        first.Register(alpha, "alpha");

        XsrRegistry<string> second = new();
        second.Register(alpha, "alpha");
        second.Register(zeta, "zeta");

        XsrRegistrySnapshot<string> firstSnapshot = first.Seal();
        XsrRegistrySnapshot<string> secondSnapshot = second.Seal();

        AssertEqual(
            RequiredRuntimeId(firstSnapshot, alpha),
            RequiredRuntimeId(secondSnapshot, alpha));
        AssertEqual(
            RequiredRuntimeId(firstSnapshot, zeta),
            RequiredRuntimeId(secondSnapshot, zeta));
        AssertEqual(1u, RequiredRuntimeId(firstSnapshot, alpha).Value);
        AssertEqual(2u, RequiredRuntimeId(firstSnapshot, zeta).Value);
    }

    private static void RegistryRejectsDuplicateSemanticIdentifiers()
    {
        XsrRegistry<string> registry = new();
        XsrSemanticId semanticId = XsrSemanticId.Parse("download.start");

        registry.Register(semanticId, "first");
        AssertThrows<InvalidOperationException>(() => registry.Register(semanticId, "second"));
    }

    private static void RegistryAcceptsConcurrentUniqueRegistrations()
    {
        XsrRegistry<int> registry = new();

        Parallel.For(0, 128, index =>
        {
            registry.Register(XsrSemanticId.Parse($"command.{index:D3}"), index);
        });

        XsrRegistrySnapshot<int> snapshot = registry.Seal();
        AssertEqual(128, snapshot.Count);
        AssertTrue(snapshot.TryGet(XsrSemanticId.Parse("command.000"), out XsrRegistryEntry<int> first));
        AssertTrue(snapshot.TryGet(XsrSemanticId.Parse("command.127"), out XsrRegistryEntry<int> last));
        AssertEqual(1u, first.RuntimeId.Value);
        AssertEqual(128u, last.RuntimeId.Value);
    }

    private static void RegistryBecomesImmutableAfterSealing()
    {
        XsrRegistry<string> registry = new();
        registry.Register(XsrSemanticId.Parse("state.publish"), "descriptor");

        XsrRegistrySnapshot<string> first = registry.Seal();
        XsrRegistrySnapshot<string> second = registry.Seal();

        AssertTrue(registry.IsSealed);
        AssertTrue(ReferenceEquals(first, second));
        AssertEqual(1, registry.Count);
        AssertThrows<InvalidOperationException>(() =>
            registry.Register(XsrSemanticId.Parse("state.other"), "other"));
    }

    private static void SnapshotResolvesBothIdentifierDirections()
    {
        XsrSemanticId semanticId = XsrSemanticId.Parse("query.settings");
        XsrRegistry<string> registry = new();
        registry.Register(semanticId, "settings-query");
        XsrRegistrySnapshot<string> snapshot = registry.Seal();

        AssertTrue(snapshot.TryGetRuntimeId(semanticId, out XsrRuntimeId runtimeId));
        AssertTrue(snapshot.TryGet(runtimeId, out XsrRegistryEntry<string> numericEntry));
        AssertTrue(snapshot.TryGet(semanticId, out XsrRegistryEntry<string> semanticEntry));
        AssertEqual("settings-query", numericEntry.Descriptor);
        AssertEqual(numericEntry, semanticEntry);
        AssertFalse(snapshot.TryGet(default(XsrRuntimeId), out _));
        AssertFalse(snapshot.TryGet(new XsrRuntimeId(999), out _));
        AssertFalse(snapshot.TryGet(XsrSemanticId.Parse("query.unknown"), out _));
    }

    private static void SnapshotSupportsConcurrentNumericReads()
    {
        XsrRegistry<string> registry = new();
        registry.Register(XsrSemanticId.Parse("event.completed"), "completed");
        XsrRegistrySnapshot<string> snapshot = registry.Seal();
        XsrRuntimeId runtimeId = RequiredRuntimeId(snapshot, XsrSemanticId.Parse("event.completed"));

        Parallel.For(0, 10_000, _ =>
        {
            if (!snapshot.TryGet(runtimeId, out XsrRegistryEntry<string> entry)
                || entry.Descriptor != "completed")
            {
                throw new InvalidOperationException("A concurrent runtime-ID lookup returned an invalid entry.");
            }
        });
    }

    private static void SnapshotNumericReadsAllocateNoManagedMemory()
    {
        XsrRegistry<string> registry = new();
        registry.Register(XsrSemanticId.Parse("state.progress"), "progress");
        XsrRegistrySnapshot<string> snapshot = registry.Seal();
        XsrRuntimeId runtimeId = RequiredRuntimeId(snapshot, XsrSemanticId.Parse("state.progress"));

        // A large warmup settles tiered compilation before the measured loop.
        for (int index = 0; index < 200_000; index++)
        {
            _ = snapshot.TryGet(runtimeId, out _);
        }

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        uint checksum = 0;
        for (int index = 0; index < 100_000; index++)
        {
            if (!snapshot.TryGet(runtimeId, out XsrRegistryEntry<string> entry))
            {
                throw new InvalidOperationException("The registered runtime ID was not found.");
            }

            checksum ^= entry.RuntimeId.Value;
        }

        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        GC.KeepAlive(checksum);
        AssertEqual(0L, allocatedBytes);
    }

    private static XsrRuntimeId RequiredRuntimeId(
        XsrRegistrySnapshot<string> snapshot,
        XsrSemanticId semanticId)
    {
        if (!snapshot.TryGetRuntimeId(semanticId, out XsrRuntimeId runtimeId))
        {
            throw new InvalidOperationException($"No runtime ID was assigned to '{semanticId}'.");
        }

        return runtimeId;
    }

    private static void AssertTrue(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected true but received false.");
        }
    }

    private static void AssertFalse(bool value) => AssertTrue(!value);

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}' but received '{actual}'.");
        }
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected exception {typeof(TException).Name}.");
    }

    private static void AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected exception {typeof(TException).Name}.");
    }
}
