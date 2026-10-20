using System.Buffers.Binary;
using System.Diagnostics;
using System.Formats.Asn1;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;
using PaperBatch.CompatibilityProbe;

if (args.Length >= 2 && args[0] == "--renderer-worker-child")
{
    if (args[1] == "exit") return 0;
    if (args[1] == "hang")
    {
        if (args.Length == 3) File.WriteAllText(args[2], Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }
}

if (args.Length == 6 && args[0] == "--source-cache-worker")
{
    return SourceIntakeTests.RunCrossProcessCacheWorker(args[1], args[2], args[3], args[4], args[5]);
}

var tests = new List<(string Name, Action Run)>
{
    ("simulated evidence does not close G1", SimulatedEvidenceDoesNotCloseG1),
    ("artifact store persists stable PDF bytes and receipt", RendererTests.ArtifactStorePersistsStablePdfAndReceipt),
    ("artifact store rejects changed PDF bytes on readback", RendererTests.ArtifactStoreRejectsChangedPdfOnReadback),
    ("artifact store rejects malformed receipts as invalid data", RendererTests.ArtifactStoreRejectsMalformedReceiptAsInvalidData),
    ("artifact store protects its Windows directory ACL", RendererTests.ArtifactStoreProtectsItsWindowsDirectoryAcl),
    ("LibreOffice private profile protects its Windows directory ACL", RendererTests.LibreOfficeProfileProtectsItsWindowsDirectoryAcl),
    ("artifact store rejects non-PDF input", RendererTests.ArtifactStoreRejectsNonPdfInput),
    ("PDF pass-through persists exact snapshot bytes without claiming page count", RendererTests.PdfPassThroughPersistsExactSnapshotBytesWithoutClaimingPageCount),
    ("built-in PNG renderer creates a readable one-page PDF from a snapshot", RendererTests.BuiltInPngRendererCreatesReadableOnePagePdfFromSnapshot),
    ("built-in PNG renderer rejects a corrupt compressed raster", RendererTests.BuiltInPngRendererRejectsCorruptCompressedRaster),
    ("built-in PNG renderer rejects alpha rather than dropping it", RendererTests.BuiltInPngRendererRejectsAlphaRatherThanDroppingIt),
    ("LibreOffice capability rejects user-writable runtime trees", RendererTests.LibreOfficeCapabilityRejectsUserWritableRuntimeTree),
    ("LibreOffice capability accepts protected trees and rejects group-writable libraries", RendererTests.LibreOfficeCapabilityAcceptsProtectedTreeAndRejectsGroupWritableLibrary),
    ("LibreOffice capability rejects a writable ancestor of a protected runtime", RendererTests.LibreOfficeCapabilityRejectsWritableAncestorOfProtectedRuntime),
    ("LibreOffice capability rejects dependency symlink escapes", RendererTests.LibreOfficeCapabilityRejectsDependencySymlinkEscape),
    ("LibreOffice rejects shared operation directories", RendererTests.LibreOfficeRejectsSharedOperationDirectory),
    ("LibreOffice rejects replaceable operation ancestors", RendererTests.LibreOfficeRejectsReplaceableOperationPathAncestors),
    ("LibreOffice profile is private and disables macros and link updates", RendererTests.LibreOfficeUsesPrivateMacroRestrictedProfile),
    ("owned process worker observes child exit", RendererTests.OwnedProcessWorkerWaitsForSuccessfulChild),
    ("owned process worker terminates child on timeout", RendererTests.OwnedProcessWorkerKillsOnlyItsChildOnTimeout),
    ("owned process worker terminates child on cancellation", RendererTests.OwnedProcessWorkerKillsItsChildOnCancellation),
    ("renderer failure does not silently fall back", RendererTests.UnsupportedFormatIsReportedWithoutFallback),
    ("missing renderer is explicitly unsupported", RendererTests.MissingAdapterIsExplicitlyUnsupported),
    ("renderer capability checks are included in the deadline", RendererTests.CapabilityCheckIsInsideTheRenderDeadline),
    ("throwing cancellation callback does not hide timeout", RendererTests.ThrowingCancellationCallbacksDoNotHideTimeout),
    ("renderer timeout has a distinct result", RendererTests.AdapterTimeoutIsDistinguished),
    ("cancelled render is not launched", RendererTests.CancelledRequestDoesNotLaunchAnAdapter),
    ("renderer capability failure does not try another adapter", RendererTests.CapabilityFailureStopsWithoutTryingAnotherAdapter),
    ("physical device output is required for G1", PhysicalDeviceOutputIsRequiredForG1),
    ("suite-specific evidence is required", SuiteSpecificEvidenceIsRequired),
    ("queue and driver inventory does not verify a print route", QueueAndDriverInventoryDoesNotVerifyPrintRoute),
    ("evidence status tokens are stable", EvidenceStatusTokensAreStable),
    ("missing native platform evidence is unavailable", MissingNativePlatformEvidenceIsUnavailable),
    ("suite coexistence distinguishes missing from unrun", SuiteCoexistenceStatusDistinguishesMissingFromUnrun),
    ("missing executable candidates do not block Office discovery", MissingExecutableCandidatesDoNotBlockOfficeDiscovery),
    ("missing explicit LibreOffice engine is unavailable", MissingExplicitLibreOfficeEngineIsUnavailable),
    ("invalid explicit LibreOffice path does not block Office or WPS evidence", InvalidExplicitLibreOfficePathDoesNotBlockOtherSuites),
    ("Winspool access errors are blocked rather than unavailable", WinspoolPathAccessErrorsAreBlocked),
    ("Office discovery does not block on named-pipe candidates", OfficeDiscoveryDoesNotBlockOnNamedPipeCandidates),
    ("PDFium inspection does not block on named-pipe candidates", PdfiumInspectionDoesNotBlockOnNamedPipeCandidates),
    ("Windows PDFium named pipes are blocked", WindowsPdfiumNamedPipeIsBlocked),
    ("Windows disk handles with device attributes are not regular files", WindowsDiskHandleDeviceAttributesAreBlocked),
    ("unsupported Unix file inspection does not open special paths", UnsupportedUnixFileInspectionDoesNotOpenCandidates),
    ("incomplete Office discovery is blocked rather than unavailable", IncompleteOfficeDiscoveryIsBlocked),
    ("Office and WPS filename candidates require product identity", OfficeAndWpsFilenameCandidatesRequireProductIdentity),
    ("suite product identity rejects substring spoofing", SuiteProductIdentityRejectsSubstringSpoofing),
    ("Office suite identity requires a trusted expected publisher", OfficeIdentityRequiresTrustedPublisher),
    ("Office signer parsing rejects non-string organization attributes", OfficeSignerOrganizationRejectsNonStringAttribute),
    ("Office trust and version metadata share one locked file identity", OfficeIdentityReadsUseOneLockedFile),
    ("Office executable digest and bitness use the same bytes", OfficeBinaryInspectionBindsDigestToArchitecture),
    ("Office measurement uses the combined binary inspection", OfficeMeasurementUsesCombinedBinaryInspection),
    ("Office and WPS size bounds run before trust verification", OfficeSizeBoundPrecedesTrustVerification),
    ("Office bitness requires matching PE optional-header magic", OfficePeArchitectureRequiresMatchingOptionalHeader),
    ("ELF architecture requires matching class and machine", ElfArchitectureRequiresMatchingClassAndMachine),
    ("ELF architecture rejects unsupported byte order", ElfArchitectureRejectsBigEndianHeaders),
    ("PDFium metadata path remains bound to the opened file", PdfiumMetadataPathRemainsBoundToOpenedFile),
    ("PDFium evidence distinguishes requested and opened paths", PdfiumEvidenceReportsRequestedAndOpenedPaths),
    ("LibreOffice launch requires a trusted direct-engine digest", LibreOfficeCandidateRequiresPinnedExecutableDigest),
    ("LibreOffice launch path remains bound to the pinned file handle", LibreOfficeLaunchRemainsBoundToPinnedFileHandle),
    ("Windows pinned launch uses the handle-resolved file path", WindowsPinnedLaunchUsesHandleResolvedPath),
    ("LibreOffice launch snapshot survives source in-place mutation", LibreOfficeSnapshotSurvivesSourceMutation),
    ("Linux LibreOffice snapshot rejects oversized sources before copying", LinuxEngineSnapshotRejectsOversizedSource),
    ("LibreOffice source hashing enforces the snapshot size limit", EngineSourceHashRejectsOversizedInput),
    ("Office and WPS candidate hashing enforces the inspection limit", OfficeAndWpsCandidateHashRejectsOversizedInput),
    ("Linux LibreOffice launch clears inherited loader overrides", LinuxLibreOfficeLaunchClearsLoaderOverrides),
    ("Linux LibreOffice alias uses the opened engine target directory", LinuxLibreOfficeAliasUsesTargetRuntimeDirectory),
    ("LibreOffice measurement distinguishes requested and opened paths", LibreOfficeMeasurementReportsRequestedAndOpenedPaths),
    ("LibreOffice identity blocks source mutation after snapshot launch", LibreOfficeIdentityBlocksSourceMutationAfterSnapshot),
    ("LibreOffice identity blocks source growth before bounded rehash", LibreOfficeIdentityBlocksOversizedSourceGrowthAfterVersion),
    ("Linux LibreOffice runtime directory stays pinned through launch", LinuxLibreOfficeRuntimeDirectoryRemainsPinned),
    ("LibreOffice runtime-component mutation blocks version evidence", LibreOfficeRuntimeComponentMutationBlocksVersionEvidence),
    ("runtime manifest rejects replaced engine entry types", RuntimeManifestRejectsReplacedEngineEntryTypes),
    ("runtime entry limit stops before materializing a directory", RuntimeEntryLimitStopsBeforeMaterializingDirectory),
    ("Linux runtime-directory resolution rejects a replacement symlink", LinuxRuntimeDirectoryResolutionRejectsSymlinkReplacement),
    ("Linux runtime-directory resolution rejects a replacement hardlink", LinuxRuntimeDirectoryResolutionRejectsHardlinkReplacement),
    ("PDFium PE exports are inspected without loading the image", PdfiumExportsAreInspected),
    ("truncated and out-of-range PE input is rejected", MalformedPeInputIsRejected),
    ("PE directory count must declare exported tables", PeDirectoryCountDeclaresExports),
    ("PE machine and optional-header bitness must agree", PeMachineAndOptionalHeaderBitnessMustAgree),
    ("mismatched PE export directory RVA and size are rejected", MalformedExportDirectoryIsRejected),
    ("declared PE export directory range must be file-backed", ExportDirectoryRangeMustBeFileBacked),
    ("explicit PDFium paths distinguish absent from unreadable", PdfiumPathStatusDistinguishesMissingFromUnreadable),
    ("unrelated soffice candidate cannot satisfy LibreOffice evidence", UnrelatedSofficeCandidateCannotSatisfyLibreOfficeEvidence),
    ("print simulation is deterministic and labeled", PrintSimulationIsDeterministic),
    ("prepared-run invariants reject invalid hashes and copy collections", CoreTests.PreparedRunInvariantsRejectInvalidHashesAndCopyCollections),
    ("prepared-run validates renderer media type syntax", CoreTests.PreparedRunValidatesRendererMediaTypeSyntax),
    ("layout rejects empty inputs and invalid ranges", LayoutTests.EmptyAndInvalidRangesAreDiagnostics),
    ("physical output faces retain sheet dimensions", LayoutTests.PhysicalFacesExposeSheetDimensionsInsteadOfNUpCells),
    ("PDF composition requires explicit static source verification", PdfCompositionTests.CompositionRequiresExplicitStaticSourceInspection),
    ("PDF composition requires source content and output dimensions", PdfCompositionTests.CompositionRequiresEveryCompiledSourceAndOutputSize),
    ("PDF composition requires explicit runtime and digest", PdfCompositionTests.CompositionRequiresAnExplicitDigestAndRuntimePath),
    ("PDF byte difference diagnostics report structural offsets without content", PdfCompositionTests.PdfDifferenceDiagnosticsReportOffsetsWithoutPdfContent),
    ("PDF output comparison ignores only trailer ID payload volatility", PdfCompositionTests.PdfOutputComparisonIgnoresOnlyTrailerIdPayloads),
    ("layout N-up and fit are deterministic", LayoutTests.NUpAndFitAreDeterministicAndImmutable),
    ("four-up cells have distinct slot rectangles", LayoutTests.FourUpUsesFourDistinctCellRectangles),
    ("page ranges and reversal preserve source mappings", LayoutTests.RangesAndReverseOrderPreserveSelectedMapping),
    ("mixed sheet sizes create separate contiguous segments", LayoutTests.MixedSheetSizesCreateDistinctContiguousSegments),
    ("duplex file boundary inserts a blank back face", LayoutTests.DuplexFileBoundariesInsertBlankBackFace),
    ("copy expansion and segment keys preserve order", LayoutTests.CopyExpansionAndSegmentsPreserveOrder),
    ("layout enforces copy and output limits", LayoutTests.OutputLimitAndCopiesAreExplicit),
    ("layout chooses rotation for mixed page orientation", LayoutTests.MixedOrientationUsesRotationWhenItFitsBetter),
    ("covers and separators are explicit layout pages", LayoutTests.CoversAndSeparatorsAreExplicitLayoutPages),
    ("duplex separators preserve the next file front side", LayoutTests.DuplexSeparatorDoesNotMoveNextFileOffFront),
    ("per-document copies distinguish covers from file separators", LayoutTests.PerDocumentCopiesKeepCoversAndFileSeparatorsDistinct),
    ("run aggregation preserves uncertainty and OS completion", CoreTests.RunAggregationPreservesUncertaintyAndOsCompletion),
    ("SQLite migrations are idempotent and preserve v1 records", CoreTests.SqliteMigrationsAreIdempotentAndPreserveV1Records),
    ("SQLite rejects future schema versions without downgrading", CoreTests.SqliteRejectsFutureSchemaVersionsWithoutDowngrading),
    ("SQLite rejects undefined persisted enum values", CoreTests.SqliteRejectsUndefinedPersistedEnumValues),
    ("run creation is atomic and persists allowlisted metadata", CoreTests.RunCreationIsAtomicAndPersistsMetadataOnly),
    ("prepared-run serialization matches golden fixture", CoreTests.PreparedRunSerializationMatchesGoldenFixture),
    ("prepared-run JSON rejects null array entries", CoreTests.PreparedRunJsonRejectsNullArrayEntries),
    ("distinct runs can reuse segment IDs", CoreTests.DistinctRunsCanReuseSegmentIds),
    ("expired runs are pruned only when retention allows it", CoreTests.ExpiredRunsArePrunedOnlyWhenRetentionAllowsIt),
    ("store initialization prunes expired runs", CoreTests.StoreInitializationPrunesExpiredRuns),
    ("run creation idempotency replays matching requests", CoreTests.RunCreationIdempotencyReplaysMatchingRequests),
    ("submission intent is durable and idempotent", CoreTests.SubmissionIntentIsDurableAndIdempotent),
    ("print capabilities fail closed and map exact settings", PrintingTests.CapabilitiesRejectUnsupportedAndMapExactOptions),
    ("capability changes stop before spool side effects", PrintingTests.CapabilityMutationStopsBeforeSubmission),
    ("mutated prepared run with same id is rejected before backend access", PrintingTests.SameRunIdWithMutatedPreparedRunIsRejectedBeforeCapabilityOrSubmission),
    ("ambiguous print submission remains unknown across restart", PrintingTests.AmbiguousSubmissionIsNotRetriedAfterRestart),
    ("print status and cancellation use exact owned job receipt", PrintingTests.StatusAndCancellationAreScopedToOwnedReceipt),
    ("concurrent print jobs keep separate submission nonces", PrintingTests.ConcurrentJobsKeepDistinctReceipts),
    ("print cancellation race preserves the winning OS status", PrintingTests.CancellationRacePreservesOsStatus),
    ("print cancellation retries the exact receipt after restart", PrintingTests.CancellationRetryAfterRestartUsesReceipt),
    ("reported completion is not paper output confirmation", PrintingTests.OsCompletionDoesNotConfirmPaperOutput),
    ("Windows queue capability enumeration is read-only", PrintingTests.NativeQueueEnumerationIsReadOnlyAndBounded),
    ("intent and OS receipt writes roll back atomically", CoreTests.IntentAndOsReceiptWritesRollBackAtomically),
    ("OS receipt is idempotent and not physical completion", CoreTests.OsReceiptIsIdempotentAndNotPhysicalCompletion),
    ("unknown outcome can still record user-confirmed paper output", CoreTests.UnknownOutcomeCanStillRecordUserConfirmedPaperOutput),
    ("recipe parser rejects invalid versions and unknown fields", CoreTests.RecipeParserRejectsInvalidVersionsAndUnknownFields),
    ("recipe serialization matches its golden fixture", CoreTests.RecipeSerializationMatchesGoldenFixture),
    ("IPC envelopes preserve correlation and negotiate capabilities", CoreTests.IpcEnvelopesPreserveCorrelationAndNegotiateCapabilities),
    ("IPC rejects malformed and oversized messages", CoreTests.IpcRejectsMalformedAndOversizedMessages),
    ("source enumeration preserves explicit order and natural folder order", SourceIntakeTests.EnumerationPreservesOrderAndNaturalFolderOrdering),
    ("source enumeration recurses only when requested and skips directory links", SourceIntakeTests.EnumerationRecursesOnlyWhenRequestedAndSkipsLinks),
    ("source enumeration reports duplicate aliases and preserves intentional copies", SourceIntakeTests.DuplicateAliasesAndIntentionalCopiesRemainVisible),
    ("source enumeration reports stable sanitized failures and cancellation", SourceIntakeTests.EnumerationReportsStableSanitizedFailuresAndCancellation),
    ("source enumeration counts invalid explicit selections toward the entry limit", SourceIntakeTests.EnumerationCountsInvalidExplicitSelectionsTowardLimit),
    ("source enumeration rejects device attributes", SourceIntakeTests.EnumerationRejectsDeviceAttributes),
    ("Windows extended local paths are not treated as network paths", SourceIntakeTests.WindowsExtendedLocalPathIsNotTreatedAsNetworkPath),
    ("source preflight recognizes supported format families", SourceIntakeTests.PreflightRecognizesSupportedFormatFamilies),
    ("source preflight rejects mismatches malformed encryption and unsafe packages", SourceIntakeTests.PreflightRejectsMismatchMalformedEncryptionAndUnsafePackages),
    ("source preflight rejects PNG files without IDAT payload", SourceIntakeTests.PreflightRejectsPngWithoutIdatPayload),
    ("source preflight rejects non-finite package compression limits", SourceIntakeTests.PreflightRejectsNonFinitePackageCompressionLimits),
    ("source preflight rejects ZIP entries without a final DEFLATE block", SourceIntakeTests.PreflightRejectsTruncatedDeflateStream),
    ("source preflight rejects bytes after a ZIP DEFLATE stream", SourceIntakeTests.PreflightRejectsTrailingDeflateBytes),
    ("source preflight follows incremental PDF encryption trailers", SourceIntakeTests.PreflightDetectsIncrementalPdfEncryption),
    ("source preflight rejects repeated TIFF array tags", SourceIntakeTests.PreflightRejectsRepeatedTiffArrayTags),
    ("source preflight bounds cumulative TIFF array work", SourceIntakeTests.PreflightBoundsCumulativeTiffArrayWork),
    ("source preflight validates legacy Office files with DIFAT sectors", SourceIntakeTests.PreflightRecognizesLegacyOfficeWithExtendedDifat),
    ("legacy Office family streams must be children of the root storage", SourceIntakeTests.PreflightRejectsLegacyOfficeFamilyStreamsInNestedStorage),
    ("source preflight detects legacy Word encryption", SourceIntakeTests.PreflightDetectsEncryptedLegacyWord),
    ("source preflight detects legacy Excel FILEPASS", SourceIntakeTests.PreflightDetectsEncryptedLegacyExcel),
    ("source preflight requires OOXML root relationships", SourceIntakeTests.PreflightRejectsOoxmlWithoutRootRelationships),
    ("source preflight requires OpenDocument content", SourceIntakeTests.PreflightRejectsOpenDocumentWithoutContent),
    ("source preflight detects OpenDocument manifest encryption", SourceIntakeTests.PreflightDetectsEncryptedOpenDocumentManifest),
    ("source preflight keeps malformed OpenDocument manifests malformed", SourceIntakeTests.PreflightRejectsMalformedOpenDocumentManifest),
    ("source preflight bounds OpenDocument manifest reads", SourceIntakeTests.PreflightBoundsOpenDocumentManifestRead),
    ("source preflight accepts a short legacy Office root mini stream", SourceIntakeTests.PreflightAcceptsLegacyOfficeMiniStreamBelowCutoff),
    ("source intake isolates a legacy Office mini-sector offset", SourceIntakeTests.IntakeIsolatesLegacyOfficeMiniStreamOffsetErrors),
    ("source snapshots hash bytes and publish path-free metadata", SourceIntakeTests.SnapshotCaptureHashesAndPublishesPathFreeMetadata),
    ("source snapshots check the Linux runtime architecture", SourceIntakeTests.SnapshotCaptureChecksLinuxRuntimeArchitecture),
    ("source snapshots reject device attributes at identity boundaries", SourceIntakeTests.SnapshotCaptureRejectsDeviceAttributeIdentities),
    ("source snapshots reject source mutation during capture", SourceIntakeTests.SnapshotCaptureRejectsSourceMutation),
    ("source snapshots fail closed without native file identity", SourceIntakeTests.SnapshotCaptureFailsClosedWithoutNativeFileIdentity),
    ("source snapshot cancellation cleans only operation temporaries", SourceIntakeTests.SnapshotCancellationCleansOnlyOperationTemporaries),
    ("source snapshot quota preserves retained entries and prunes only released entries", SourceIntakeTests.SnapshotQuotaAndRetentionAreSafe),
    ("source snapshot quota recovers owned orphan temporary directories", SourceIntakeTests.SnapshotQuotaReclaimsOwnedOrphanTemporaryDirectories),
    ("source snapshot quota recovers owned metadata temporary files", SourceIntakeTests.SnapshotQuotaReclaimsOwnedMetadataTemporaryFiles),
    ("source snapshot quota is enforced across processes", SourceIntakeTests.SnapshotQuotaIsEnforcedAcrossProcesses),
    ("source snapshot reads verify length and digest", SourceIntakeTests.OpenReadVerifiesSnapshotIntegrity),
    ("source snapshot rejects paths beneath reparse-point ancestors", SourceIntakeTests.SnapshotCaptureRejectsReparsePointAncestor),
    ("source identity failures remain isolated to one intake item", SourceIntakeTests.SnapshotIdentityFailureDoesNotAbortBatchItems),
    ("source preflight read failures stay isolated and release snapshots", SourceIntakeTests.SnapshotPreflightReadFailureDoesNotAbortBatchItems),
    ("source intake prepares valid snapshots while retaining per-item errors", SourceIntakeTests.IntakePreparesSnapshotsAndKeepsPerItemErrors)
};

if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PAPERBATCH_PDFIUM_TEST_PATH")))
    tests.Add(("PDFium vector composition preserves transformed N-up placement", PdfCompositionTests.NativeCompositionRendersTwoUpFixture));

if (OperatingSystem.IsLinux())
{
    tests.Add(("LibreOffice version timeout stops its owned process", RendererTests.LibreOfficeVersionQueryStopsOnTimeout));
    if (Environment.GetEnvironmentVariable("PAPERBATCH_REQUIRE_LIBREOFFICE_TEST") == "1")
        tests.Add(("pinned stable LibreOffice renders an immutable synthetic DOCX", RendererTests.PinnedLibreOfficeRendersSyntheticDocx));
    else
        Console.WriteLine("SKIP pinned stable LibreOffice integration (CI exact-engine configuration required)");
}
else
    Console.WriteLine("SKIP pinned stable LibreOffice integration (Linux exact-engine CI only)");

var failures = 0;
foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures++;
        if (name == "PDFium vector composition preserves transformed N-up placement")
            Console.Error.WriteLine($"FAIL {name}: {exception}");
        else
            Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

return failures == 0 ? 0 : 1;

static void SimulatedEvidenceDoesNotCloseG1()
{
    var native = ProbeEvidence.Verified(EvidenceSource.Native, "fixture", "native result");
    var simulation = ProbeEvidence.Verified(EvidenceSource.Simulation, "simulator", "SIM-0001");
    var gate = G1GateEvaluator.Evaluate(new G1Evidence(
        native,
        native,
        native,
        native,
        native,
        native,
        native,
        native,
        native,
        simulation,
        native));

    Assert.Equal("open", gate.Status);
    Assert.Contains("GDI job-ID behavior", gate.MissingRequirements);

    var allNative = G1GateEvaluator.Evaluate(new G1Evidence(native, native, native, native, native, native, native, native, native, native, native));
    Assert.Equal("complete", allNative.Status);
}

static void PhysicalDeviceOutputIsRequiredForG1()
{
    var native = ProbeEvidence.Verified(EvidenceSource.Native, "fixture", "native result");
    var unavailable = ProbeEvidence.Unavailable(
        EvidenceSource.Native,
        "physical-device output confirmation",
        "No physical printer and operator-verified output sheet were available.");
    var gate = G1GateEvaluator.Evaluate(new G1Evidence(
        PdfiumComposition: native,
        PdfiumForms: native,
        MicrosoftOfficeExport: native,
        WpsExport: native,
        LibreOfficeExport: native,
        MicrosoftOfficeCoexistence: native,
        WpsCoexistence: native,
        LibreOfficeCoexistence: native,
        GdiQueueDriverRoute: native,
        GdiJobId: native,
        PhysicalDeviceOutput: unavailable));

    Assert.Equal("open", gate.Status);
    Assert.Contains("physical device output", gate.MissingRequirements);

    var allNative = G1GateEvaluator.Evaluate(new G1Evidence(
        PdfiumComposition: native,
        PdfiumForms: native,
        MicrosoftOfficeExport: native,
        WpsExport: native,
        LibreOfficeExport: native,
        MicrosoftOfficeCoexistence: native,
        WpsCoexistence: native,
        LibreOfficeCoexistence: native,
        GdiQueueDriverRoute: native,
        GdiJobId: native,
        PhysicalDeviceOutput: native));
    Assert.Equal("complete", allNative.Status);
}

static void SuiteSpecificEvidenceIsRequired()
{
    var native = ProbeEvidence.Verified(EvidenceSource.Native, "fixture", "native result");
    var notRun = ProbeEvidence.NotRun(EvidenceSource.Native, "fixture", "suite-specific fixture not run");
    var gate = G1GateEvaluator.Evaluate(new G1Evidence(
        PdfiumComposition: native,
        PdfiumForms: native,
        MicrosoftOfficeExport: notRun,
        WpsExport: notRun,
        LibreOfficeExport: native,
        MicrosoftOfficeCoexistence: native,
        WpsCoexistence: native,
        LibreOfficeCoexistence: native,
        GdiQueueDriverRoute: native,
        GdiJobId: native,
        PhysicalDeviceOutput: native));

    Assert.Equal("open", gate.Status);
    Assert.Contains("Microsoft Office document export", gate.MissingRequirements);
    Assert.Contains("WPS document export", gate.MissingRequirements);
    Assert.True(!gate.MissingRequirements.Contains("LibreOffice document export"));
}

static void QueueAndDriverInventoryDoesNotVerifyPrintRoute()
{
    var native = ProbeEvidence.Verified(EvidenceSource.Native, "fixture", "native result");
    var routeNotRun = ProbeEvidence.NotRun(
        EvidenceSource.Native,
        "validate an actual queue/driver/port route",
        "Queue and driver inventory does not verify that a print job can route to a device.");
    var printing = new PrintProbeResult(native, native, native, routeNotRun, native, native);
    var gate = G1GateEvaluator.Evaluate(new G1Evidence(
        PdfiumComposition: native,
        PdfiumForms: native,
        MicrosoftOfficeExport: native,
        WpsExport: native,
        LibreOfficeExport: native,
        MicrosoftOfficeCoexistence: native,
        WpsCoexistence: native,
        LibreOfficeCoexistence: native,
        GdiQueueDriverRoute: printing.GdiQueueDriverRoute,
        GdiJobId: printing.GdiJobId,
        PhysicalDeviceOutput: printing.PhysicalDeviceOutput));

    Assert.Equal(EvidenceStatus.Verified, printing.Queues.Status);
    Assert.Equal(EvidenceStatus.Verified, printing.Drivers.Status);
    Assert.Equal("open", gate.Status);
    Assert.Contains("GDI queue and driver route", gate.MissingRequirements);
}

static void PdfiumExportsAreInspected()
{
    var image = CreatePeImage(
        "FPDF_InitLibrary",
        "FPDF_LoadDocument",
        "FPDF_ImportPagesByIndex",
        "FPDFDOC_InitFormFillEnvironment",
        "FPDF_FFLDraw");

    var parsed = PeImageParser.Parse(image);
    var capabilities = PdfiumCapabilityClassifier.Classify(parsed.ExportNames);

    Assert.Equal("x64", parsed.Architecture);
    Assert.True(capabilities.CoreApi);
    Assert.True(capabilities.PageImportApi);
    Assert.True(capabilities.FormFillApi);
}

static void EvidenceStatusTokensAreStable()
{
    var json = ProbeJson.Serialize(ProbeEvidence.NotRun(EvidenceSource.Native, "fixture", "not started"));
    Assert.Contains("\"status\": \"not-run\"", json);
}

static void MissingNativePlatformEvidenceIsUnavailable()
{
    var pdfium = PdfiumProbe.Collect(null);
    Assert.Equal(EvidenceStatus.Unavailable, pdfium.CompositionBehavior.Status);
    Assert.Equal(EvidenceStatus.Unavailable, pdfium.FormBehavior.Status);

    var printing = PrintProbe.Collect(includeLocalQueues: false);
    if (!OperatingSystem.IsWindows())
    {
        Assert.Equal(EvidenceStatus.Unavailable, printing.Backend.Status);
        Assert.Equal(EvidenceStatus.Unavailable, printing.GdiJobId.Status);
        Assert.Equal(EvidenceStatus.Unavailable, printing.PhysicalDeviceOutput.Status);
    }
    else
    {
        Assert.Equal(EvidenceStatus.NotRun, printing.GdiJobId.Status);
        Assert.Equal(EvidenceStatus.NotRun, printing.PhysicalDeviceOutput.Status);
    }
}

static void MalformedPeInputIsRejected()
{
    Assert.Throws<InvalidDataException>(() => PeImageParser.Parse(new byte[] { (byte)'M', (byte)'Z' }));

    var image = CreatePeImage("FPDF_InitLibrary");
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x188 + 20, 4), uint.MaxValue);
    Assert.Throws<InvalidDataException>(() => PeImageParser.Parse(image));

    var oversizedHeaderRva = CreatePeImage("FPDF_InitLibrary");
    BinaryPrimitives.WriteUInt32LittleEndian(oversizedHeaderRva.AsSpan(0x98 + 60, 4), uint.MaxValue);
    BinaryPrimitives.WriteUInt32LittleEndian(oversizedHeaderRva.AsSpan(0x98 + 112, 4), 0xF0000000);
    Assert.Throws<InvalidDataException>(() => PeImageParser.Parse(oversizedHeaderRva));

    var emptyFunctionTable = CreatePeImage("FPDF_InitLibrary");
    BinaryPrimitives.WriteUInt32LittleEndian(emptyFunctionTable.AsSpan(0x200 + 20, 4), 0);
    Assert.Throws<InvalidDataException>(() => PeImageParser.Parse(emptyFunctionTable));

    var missingFunctionsAddress = CreatePeImage("FPDF_InitLibrary");
    BinaryPrimitives.WriteUInt32LittleEndian(missingFunctionsAddress.AsSpan(0x200 + 28, 4), 0);
    Assert.Throws<InvalidDataException>(() => PeImageParser.Parse(missingFunctionsAddress));

    var missingNamesAddress = CreatePeImage("FPDF_InitLibrary");
    BinaryPrimitives.WriteUInt32LittleEndian(missingNamesAddress.AsSpan(0x200 + 32, 4), 0);
    Assert.Throws<InvalidDataException>(() => PeImageParser.Parse(missingNamesAddress));

    var missingOrdinalsAddress = CreatePeImage("FPDF_InitLibrary");
    BinaryPrimitives.WriteUInt32LittleEndian(missingOrdinalsAddress.AsSpan(0x200 + 36, 4), 0);
    Assert.Throws<InvalidDataException>(() => PeImageParser.Parse(missingOrdinalsAddress));

    var invalidOrdinal = CreatePeImage("FPDF_InitLibrary");
    BinaryPrimitives.WriteUInt16LittleEndian(invalidOrdinal.AsSpan(0x200 + 0x80, 2), 1);
    Assert.Throws<InvalidDataException>(() => PeImageParser.Parse(invalidOrdinal));
}

static void MalformedExportDirectoryIsRejected()
{
    var missingExportRva = CreatePeImage("FPDF_InitLibrary");
    BinaryPrimitives.WriteUInt32LittleEndian(missingExportRva.AsSpan(0x98 + 112, 4), 0);
    Assert.Throws<InvalidDataException>(() => PeImageParser.Parse(missingExportRva));

    var missingExportSize = CreatePeImage("FPDF_InitLibrary");
    BinaryPrimitives.WriteUInt32LittleEndian(missingExportSize.AsSpan(0x98 + 116, 4), 0);
    Assert.Throws<InvalidDataException>(() => PeImageParser.Parse(missingExportSize));
}

static void ExportDirectoryRangeMustBeFileBacked()
{
    var truncatedExportDirectory = CreatePeImage();
    BinaryPrimitives.WriteUInt32LittleEndian(truncatedExportDirectory.AsSpan(0x98 + 116, 4), 0x401);
    Assert.Throws<InvalidDataException>(() => PeImageParser.Parse(truncatedExportDirectory));
}

static void PdfiumPathStatusDistinguishesMissingFromUnreadable()
{
    var missingPath = Path.Combine(Path.GetTempPath(), "paperbatch-missing-pdfium-" + Guid.NewGuid().ToString("N") + ".dll");
    var missing = PdfiumProbe.Collect(missingPath);
    Assert.Equal(EvidenceStatus.Unavailable, missing.Identity.Status);

    var directoryPath = Path.Combine(Path.GetTempPath(), "paperbatch-pdfium-directory-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directoryPath);
    try
    {
        var directory = PdfiumProbe.Collect(directoryPath);
        Assert.Equal(EvidenceStatus.Blocked, directory.Identity.Status);
        Assert.Equal(EvidenceStatus.Blocked, directory.ApiExports.Status);
    }
    finally
    {
        Directory.Delete(directoryPath);
    }
}

static void WindowsPdfiumNamedPipeIsBlocked()
{
    if (!OperatingSystem.IsWindows())
    {
        return;
    }

    var pipeName = "PaperBatchPdfium" + Guid.NewGuid().ToString("N");
    using var server = new NamedPipeServerStream(
        pipeName,
        PipeDirection.InOut,
        maxNumberOfServerInstances: 1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous);
    var connection = server.WaitForConnectionAsync();
    var inspection = Task.Run(() => PdfiumProbe.Collect($@"\\.\pipe\{pipeName}"));
    if (!inspection.Wait(TimeSpan.FromSeconds(5)))
    {
        throw new InvalidOperationException("PDFium inspection did not return for a named-pipe path.");
    }

    if (!connection.Wait(TimeSpan.FromSeconds(5)))
    {
        throw new InvalidOperationException("The named-pipe fixture did not connect to the PDFium probe.");
    }

    var result = inspection.GetAwaiter().GetResult();
    Assert.Equal(EvidenceStatus.Blocked, result.Identity.Status);
    Assert.Equal(EvidenceStatus.Blocked, result.ApiExports.Status);
}

static void WindowsDiskHandleDeviceAttributesAreBlocked()
{
    Assert.True(LockedFilePath.IsWindowsRegularFileType(LockedFilePath.WindowsFileTypeDisk, FileAttributes.Archive));
    Assert.True(!LockedFilePath.IsWindowsRegularFileType(LockedFilePath.WindowsFileTypeDisk, FileAttributes.Device));
    Assert.True(!LockedFilePath.IsWindowsRegularFileType(LockedFilePath.WindowsFileTypeDisk, FileAttributes.Directory));
    Assert.True(!LockedFilePath.IsWindowsRegularFileType(2, FileAttributes.Archive));
}

static void UnsupportedUnixFileInspectionDoesNotOpenCandidates()
{
    if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
    {
        return;
    }

    var directory = Path.Combine(Path.GetTempPath(), "paperbatch-unsupported-unix-pdfium-fifo-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var pipePath = Path.Combine(directory, "pdfium.dll");
    if (TestNativeMethods.MkFifo(pipePath, 0x180) != 0)
    {
        throw new InvalidOperationException($"Could not create the unsupported-Unix named pipe (errno {Marshal.GetLastPInvokeError()}).");
    }

    try
    {
        var collection = Task.Run(() => PdfiumProbe.Collect(pipePath));
        if (!collection.Wait(TimeSpan.FromSeconds(3)))
        {
            using (new FileStream(pipePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
            }
            collection.Wait(TimeSpan.FromSeconds(3));
            throw new InvalidOperationException("PDFium inspection blocked while opening a special path on an unsupported Unix platform.");
        }

        Assert.Equal(EvidenceStatus.Blocked, collection.GetAwaiter().GetResult().Identity.Status);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void PeDirectoryCountDeclaresExports()
{
    var undeclaredExportDirectory = CreatePeImage("FPDF_InitLibrary");
    BinaryPrimitives.WriteUInt32LittleEndian(undeclaredExportDirectory.AsSpan(0x98 + 108, 4), 0);
    Assert.Throws<InvalidDataException>(() => PeImageParser.Parse(undeclaredExportDirectory));
}

static void PeMachineAndOptionalHeaderBitnessMustAgree()
{
    var x64MachineWithPe32Header = CreatePeImage("FPDF_InitLibrary");
    BinaryPrimitives.WriteUInt16LittleEndian(x64MachineWithPe32Header.AsSpan(0x98, 2), 0x10B);
    Assert.Throws<InvalidDataException>(() => PeImageParser.Parse(x64MachineWithPe32Header));

    var x86MachineWithPe32PlusHeader = CreatePeImage("FPDF_InitLibrary");
    BinaryPrimitives.WriteUInt16LittleEndian(x86MachineWithPe32PlusHeader.AsSpan(0x84, 2), 0x014C);
    Assert.Throws<InvalidDataException>(() => PeImageParser.Parse(x86MachineWithPe32PlusHeader));
}

static void OfficePeArchitectureRequiresMatchingOptionalHeader()
{
    var x64MachineWithPe32Header = CreatePeImage("FPDF_InitLibrary");
    BinaryPrimitives.WriteUInt16LittleEndian(x64MachineWithPe32Header.AsSpan(0x98, 2), 0x10B);
    using var firstImage = new MemoryStream(x64MachineWithPe32Header);
    Assert.Equal(null, OfficeProbe.InspectBinaryWithinLimit(firstImage, 4096).Architecture);

    var x86MachineWithPe32PlusHeader = CreatePeImage("FPDF_InitLibrary");
    BinaryPrimitives.WriteUInt16LittleEndian(x86MachineWithPe32PlusHeader.AsSpan(0x84, 2), 0x014C);
    using var secondImage = new MemoryStream(x86MachineWithPe32PlusHeader);
    Assert.Equal(null, OfficeProbe.InspectBinaryWithinLimit(secondImage, 4096).Architecture);
}

static void ElfArchitectureRequiresMatchingClassAndMachine()
{
    var x86MachineWith64BitClass = new byte[20];
    x86MachineWith64BitClass[0] = 0x7F;
    x86MachineWith64BitClass[1] = (byte)'E';
    x86MachineWith64BitClass[2] = (byte)'L';
    x86MachineWith64BitClass[3] = (byte)'F';
    x86MachineWith64BitClass[4] = 2;
    BinaryPrimitives.WriteUInt16LittleEndian(x86MachineWith64BitClass.AsSpan(18, 2), 3);
    using var image = new MemoryStream(x86MachineWith64BitClass);

    Assert.Equal(null, OfficeProbe.InspectBinaryWithinLimit(image, 4096).Architecture);
}

static void ElfArchitectureRejectsBigEndianHeaders()
{
    var bigEndianX86_64 = new byte[20];
    bigEndianX86_64[0] = 0x7F;
    bigEndianX86_64[1] = (byte)'E';
    bigEndianX86_64[2] = (byte)'L';
    bigEndianX86_64[3] = (byte)'F';
    bigEndianX86_64[4] = 2;
    bigEndianX86_64[5] = 2;
    bigEndianX86_64[18] = 0x3E;
    bigEndianX86_64[19] = 0;
    using var image = new MemoryStream(bigEndianX86_64);

    Assert.Equal(null, OfficeProbe.InspectBinaryWithinLimit(image, 4096).Architecture);
}

static void OfficeSizeBoundPrecedesTrustVerification()
{
    var directory = Path.Combine(Path.GetTempPath(), "paperbatch-office-trust-size-limit-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var oversizedPath = Path.Combine(directory, "WINWORD.EXE");
    var boundaryPath = Path.Combine(directory, "wps.exe");
    File.WriteAllBytes(oversizedPath, new byte[9]);
    File.WriteAllBytes(boundaryPath, new byte[8]);
    try
    {
        var trustCalls = 0;
        using (var oversizedFile = new FileStream(oversizedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var oversized = OfficeProbe.ReadOfficeVersionWithinLimit(
                oversizedFile,
                maximumBytes: 8,
                () =>
                {
                    trustCalls++;
                    return ProbeEvidence.Verified(EvidenceSource.Native, "test trust verifier", "trusted");
                });
            Assert.Equal(EvidenceStatus.Blocked, oversized.Status);
            Assert.Contains("inspection limit", oversized.Limitation!);
            Assert.Equal(0, trustCalls);
        }

        using (var boundaryFile = new FileStream(boundaryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var boundary = OfficeProbe.ReadOfficeVersionWithinLimit(
                boundaryFile,
                maximumBytes: 8,
                () =>
                {
                    trustCalls++;
                    return ProbeEvidence.Verified(EvidenceSource.Native, "test trust verifier", "trusted");
                });
            Assert.Equal(EvidenceStatus.Verified, boundary.Status);
            Assert.Equal(1, trustCalls);
        }
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void SuiteCoexistenceStatusDistinguishesMissingFromUnrun()
{
    if (OperatingSystem.IsWindows())
    {
        return;
    }

    var previousPath = Environment.GetEnvironmentVariable("PATH");
    var directory = Path.Combine(Path.GetTempPath(), "paperbatch-office-probe-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    CreateShellExecutable(Path.Combine(directory, "soffice"), "LibreOffice launcher");
    var enginePath = Path.Combine(directory, "soffice.bin");
    CreateShellExecutable(enginePath, "LibreOffice 1.2.3");
    try
    {
        Environment.SetEnvironmentVariable("PATH", directory);
        var unpinned = OfficeProbe.Collect();
        Assert.Equal(EvidenceStatus.Blocked, unpinned.Coexistence.LibreOffice.Status);
        Assert.Equal(EvidenceStatus.Blocked, unpinned.Exports.LibreOffice.Status);

        var pinned = OfficeProbe.Collect(
            expectedLibreOfficeEngineSha256: Sha256File(enginePath),
            libreOfficeEnginePath: enginePath);
        Assert.Equal(EvidenceStatus.NotRun, pinned.Coexistence.LibreOffice.Status);
        Assert.Equal(EvidenceStatus.NotRun, pinned.Exports.LibreOffice.Status);
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", previousPath);
        Directory.Delete(directory, recursive: true);
    }
}

static void IncompleteOfficeDiscoveryIsBlocked()
{
    if (!OperatingSystem.IsLinux())
    {
        return;
    }

    var previousPath = Environment.GetEnvironmentVariable("PATH");
    var directory = Path.Combine(Path.GetTempPath(), "paperbatch-office-discovery-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var loop = Path.Combine(directory, "loop");
    Directory.CreateSymbolicLink(loop, loop);
    try
    {
        Environment.SetEnvironmentVariable("PATH", loop);
        var result = OfficeProbe.Collect();
        Assert.Equal(EvidenceStatus.Blocked, result.InstalledSuites.Status);
        Assert.Equal(EvidenceStatus.Blocked, result.Exports.MicrosoftOffice.Status);
        Assert.Equal(EvidenceStatus.Blocked, result.Exports.WpsOffice.Status);
        Assert.Equal(EvidenceStatus.Blocked, result.Exports.LibreOffice.Status);
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", previousPath);
        File.Delete(loop);
        Directory.Delete(directory);
    }
}

static void MissingExecutableCandidatesDoNotBlockOfficeDiscovery()
{
    if (!OperatingSystem.IsLinux())
    {
        return;
    }

    var previousPath = Environment.GetEnvironmentVariable("PATH");
    var directory = Path.Combine(Path.GetTempPath(), "paperbatch-office-empty-path-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        Environment.SetEnvironmentVariable("PATH", directory);
        var result = OfficeProbe.Collect();
        Assert.Equal(EvidenceStatus.Unavailable, result.InstalledSuites.Status);
        Assert.Equal(EvidenceStatus.Unavailable, result.Exports.MicrosoftOffice.Status);
        Assert.Equal(EvidenceStatus.Unavailable, result.Exports.WpsOffice.Status);
        Assert.Equal(EvidenceStatus.Unavailable, result.Exports.LibreOffice.Status);
        Assert.Equal(EvidenceStatus.Unavailable, result.Coexistence.MicrosoftOffice.Status);
        Assert.Equal(EvidenceStatus.Unavailable, result.Coexistence.WpsOffice.Status);
        Assert.Equal(EvidenceStatus.Unavailable, result.Coexistence.LibreOffice.Status);
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", previousPath);
        Directory.Delete(directory, recursive: true);
    }
}

static void MissingExplicitLibreOfficeEngineIsUnavailable()
{
    if (!OperatingSystem.IsLinux())
    {
        return;
    }

    var previousPath = Environment.GetEnvironmentVariable("PATH");
    var directory = Path.Combine(Path.GetTempPath(), "paperbatch-office-missing-engine-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        Environment.SetEnvironmentVariable("PATH", directory);
        var missingEngine = Path.Combine(directory, "soffice.bin");
        var result = OfficeProbe.Collect(new string('0', 64), missingEngine);
        Assert.Equal(EvidenceStatus.Unavailable, result.InstalledSuites.Status);
        Assert.Equal(EvidenceStatus.Unavailable, result.Exports.LibreOffice.Status);
        Assert.Equal(EvidenceStatus.Unavailable, result.Coexistence.LibreOffice.Status);
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", previousPath);
        Directory.Delete(directory, recursive: true);
    }
}

static void InvalidExplicitLibreOfficePathDoesNotBlockOtherSuites()
{
    if (!OperatingSystem.IsLinux())
    {
        return;
    }

    var previousPath = Environment.GetEnvironmentVariable("PATH");
    var directory = Path.Combine(Path.GetTempPath(), "paperbatch-office-invalid-engine-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        Environment.SetEnvironmentVariable("PATH", directory);
        var result = OfficeProbe.Collect(new string('0', 64), directory);

        Assert.Equal(EvidenceStatus.Blocked, result.Exports.LibreOffice.Status);
        Assert.Equal(EvidenceStatus.Blocked, result.Coexistence.LibreOffice.Status);
        Assert.Equal(EvidenceStatus.Unavailable, result.Exports.MicrosoftOffice.Status);
        Assert.Equal(EvidenceStatus.Unavailable, result.Exports.WpsOffice.Status);
        Assert.Equal(EvidenceStatus.Unavailable, result.Coexistence.MicrosoftOffice.Status);
        Assert.Equal(EvidenceStatus.Unavailable, result.Coexistence.WpsOffice.Status);
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", previousPath);
        Directory.Delete(directory, recursive: true);
    }
}

static void WinspoolPathAccessErrorsAreBlocked()
{
    foreach (var error in new Exception[]
    {
        new UnauthorizedAccessException("access denied"),
        new IOException("system library access failed")
    })
    {
        var evidence = WindowsPrintProbe.InspectWinspoolPath("winspool.drv", _ => throw error);
        Assert.Equal(EvidenceStatus.Blocked, evidence.Status);
    }

    var missing = WindowsPrintProbe.InspectWinspoolPath("winspool.drv", _ => throw new FileNotFoundException());
    Assert.Equal(EvidenceStatus.Unavailable, missing.Status);
}

static void OfficeDiscoveryDoesNotBlockOnNamedPipeCandidates()
{
    if (!OperatingSystem.IsLinux())
    {
        return;
    }

    var previousPath = Environment.GetEnvironmentVariable("PATH");
    var directory = Path.Combine(Path.GetTempPath(), "paperbatch-office-fifo-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var pipePath = Path.Combine(directory, "soffice");
    if (TestNativeMethods.MkFifo(pipePath, 0x180) != 0)
    {
        throw new InvalidOperationException($"Could not create named pipe for regression test (errno {Marshal.GetLastPInvokeError()}).");
    }

    try
    {
        Environment.SetEnvironmentVariable("PATH", directory);
        var collection = Task.Run(() => OfficeProbe.Collect());
        if (!collection.Wait(TimeSpan.FromSeconds(3)))
        {
            using (new FileStream(pipePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
            }
            collection.Wait(TimeSpan.FromSeconds(3));
            throw new InvalidOperationException("Office discovery blocked while opening a named-pipe executable candidate.");
        }

        var result = collection.GetAwaiter().GetResult();
        Assert.True(result.Exports.LibreOffice.Status != EvidenceStatus.Verified);
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", previousPath);
        Directory.Delete(directory, recursive: true);
    }
}

static void PdfiumInspectionDoesNotBlockOnNamedPipeCandidates()
{
    if (!OperatingSystem.IsLinux())
    {
        return;
    }

    var directory = Path.Combine(Path.GetTempPath(), "paperbatch-pdfium-fifo-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var pipePath = Path.Combine(directory, "pdfium.dll");
    if (TestNativeMethods.MkFifo(pipePath, 0x180) != 0)
    {
        throw new InvalidOperationException($"Could not create PDFium named pipe for regression test (errno {Marshal.GetLastPInvokeError()}).");
    }

    try
    {
        var collection = Task.Run(() => PdfiumProbe.Collect(pipePath));
        if (!collection.Wait(TimeSpan.FromSeconds(3)))
        {
            using (new FileStream(pipePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
            }
            collection.Wait(TimeSpan.FromSeconds(3));
            throw new InvalidOperationException("PDFium inspection blocked while opening a named-pipe candidate.");
        }

        var result = collection.GetAwaiter().GetResult();
        Assert.Equal(EvidenceStatus.Blocked, result.Identity.Status);
        Assert.Equal(EvidenceStatus.Blocked, result.ApiExports.Status);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void OfficeAndWpsFilenameCandidatesRequireProductIdentity()
{
    if (!OperatingSystem.IsLinux())
    {
        return;
    }

    var previousPath = Environment.GetEnvironmentVariable("PATH");
    var directory = Path.Combine(Path.GetTempPath(), "paperbatch-office-identity-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, "WINWORD.EXE"), "unrelated application");
    File.WriteAllText(Path.Combine(directory, "wps.exe"), "unrelated application");
    try
    {
        Environment.SetEnvironmentVariable("PATH", directory);
        var result = OfficeProbe.Collect();
        var inventory = ProbeJson.Serialize(result.InstalledSuites);

        Assert.Contains("Unverified executable candidate", inventory);
        Assert.True(!inventory.Contains("\"suite\": \"Microsoft Office\"", StringComparison.Ordinal));
        Assert.True(!inventory.Contains("\"suite\": \"WPS Office\"", StringComparison.Ordinal));
        Assert.Equal(EvidenceStatus.Blocked, result.Exports.MicrosoftOffice.Status);
        Assert.Equal(EvidenceStatus.Blocked, result.Exports.WpsOffice.Status);
        Assert.Equal(EvidenceStatus.Blocked, result.Coexistence.MicrosoftOffice.Status);
        Assert.Equal(EvidenceStatus.Blocked, result.Coexistence.WpsOffice.Status);
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", previousPath);
        Directory.Delete(directory, recursive: true);
    }
}

static void SuiteProductIdentityRejectsSubstringSpoofing()
{
    Assert.True(OfficeProbe.HasExpectedOfficeIdentity("Microsoft Office", "Microsoft Office", "Microsoft Corporation", "Microsoft Corporation", true));
    Assert.True(OfficeProbe.HasExpectedOfficeIdentity("Microsoft Office", "Word 2024", "Microsoft Corporation", "Microsoft Corporation", true));
    Assert.True(OfficeProbe.HasExpectedOfficeIdentity("WPS Office", "WPS Office", "Zhuhai Kingsoft Office Software Co.,Ltd.", "Zhuhai Kingsoft Office Software Co.,Ltd.", true));
    Assert.True(OfficeProbe.HasExpectedOfficeIdentity("WPS Office", "Kingsoft Office 2019", "Zhuhai Kingsoft Office Software Co.,Ltd.", "Zhuhai Kingsoft Office Software Co.,Ltd.", true));
    Assert.True(!OfficeProbe.HasExpectedOfficeIdentity("Microsoft Office", "NotOffice", "NotMicrosoft", "Microsoft Corporation", true));
    Assert.True(!OfficeProbe.HasExpectedOfficeIdentity("WPS Office", "NotWPS", "NotKingsoft", "Zhuhai Kingsoft Office Software Co.,Ltd.", true));
    Assert.True(!OfficeProbe.HasExpectedOfficeIdentity("Microsoft Office", "Office", "Contoso Microsoftish", "Microsoft Corporation", true));
    Assert.True(!OfficeProbe.HasExpectedOfficeIdentity("WPS Office", "NotKingsoft Office", "Zhuhai Kingsoft Office Software Co.,Ltd.", "Zhuhai Kingsoft Office Software Co.,Ltd.", true));
}

static void OfficeIdentityRequiresTrustedPublisher()
{
    Assert.True(!OfficeProbe.HasExpectedOfficeIdentity(
        "Microsoft Office", "Microsoft Office", "Microsoft Corporation", "Microsoft Corporation", false));
    Assert.True(!OfficeProbe.HasExpectedOfficeIdentity(
        "Microsoft Office", "Microsoft Office", "Microsoft Corporation", "Contoso Microsoft Services", true));
    Assert.True(!OfficeProbe.HasExpectedOfficeIdentity(
        "WPS Office", "WPS Office", "Zhuhai Kingsoft Office Software Co.,Ltd.", "Kingsoft-adjacent Vendor", true));
}

static void OfficeSignerOrganizationRejectsNonStringAttribute()
{
    var writer = new AsnWriter(AsnEncodingRules.DER);
    using (writer.PushSequence())
    {
        using (writer.PushSetOf())
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier("2.5.4.10");
                writer.WriteInteger(17);
            }
        }
    }

    var subjectName = new X500DistinguishedName(writer.Encode());
    Assert.True(OfficeProbe.ReadSignerOrganization(subjectName) is null);

    var validWriter = new AsnWriter(AsnEncodingRules.DER);
    using (validWriter.PushSequence())
    {
        using (validWriter.PushSetOf())
        {
            using (validWriter.PushSequence())
            {
                validWriter.WriteObjectIdentifier("2.5.4.10");
                validWriter.WriteCharacterString(UniversalTagNumber.UTF8String, "Microsoft Corporation");
            }
        }
    }

    Assert.Equal(
        "Microsoft Corporation",
        OfficeProbe.ReadSignerOrganization(new X500DistinguishedName(validWriter.Encode())));
}

static void OfficeIdentityReadsUseOneLockedFile()
{
    if (!OperatingSystem.IsLinux())
    {
        return;
    }

    var directory = Path.Combine(Path.GetTempPath(), "paperbatch-office-identity-lock-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "candidate.exe");
    var originalPath = Path.Combine(directory, "original.exe");
    var replacementPath = Path.Combine(directory, "replacement.exe");
    File.WriteAllText(originalPath, "original signed image");
    File.WriteAllText(replacementPath, "replacement unsigned image");
    File.CreateSymbolicLink(path, originalPath);
    SafeFileHandle? trustHandle = null;
    var sameOpenHandle = false;
    try
    {
        var values = OfficeProbe.ReadOfficeIdentityWithLockedFile(
            path,
            (lockedPath, handle) =>
            {
                trustHandle = handle;
                Assert.Equal("original signed image", File.ReadAllText(lockedPath));
                File.Delete(path);
                File.CreateSymbolicLink(path, replacementPath);
                return "trusted";
            },
            (lockedPath, handle) =>
            {
                sameOpenHandle = ReferenceEquals(trustHandle, handle) && !handle.IsClosed && !handle.IsInvalid;
                Assert.Equal("original signed image", File.ReadAllText(lockedPath));
                return "version-resource";
            });

        Assert.Equal("trusted", values.Identity);
        Assert.Equal("version-resource", values.Metadata);
        Assert.True(sameOpenHandle);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void OfficeBinaryInspectionBindsDigestToArchitecture()
{
    var original = CreatePeImage("FPDF_InitLibrary");
    var replacement = (byte[])original.Clone();
    BinaryPrimitives.WriteUInt16LittleEndian(replacement.AsSpan(0x80 + 4, 2), 0x014C);
    using var source = new RewritingSeekableStream(original, replacement);

    var result = OfficeProbe.InspectBinaryWithinLimit(source, maximumBytes: 4096);

    Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original)).ToLowerInvariant(), result.Sha256);
    Assert.Equal("x64 (64-bit)", result.Architecture);
    Assert.Equal(original.LongLength, result.SizeBytes);
    Assert.Equal(0, source.Rewinds);

    var elf = CreateElfImage(elfClass: 2, machine: 62);
    using var elfStream = new MemoryStream(elf);
    var elfInspection = OfficeProbe.InspectBinaryWithinLimit(elfStream, maximumBytes: 1024);
    Assert.Equal("x64 (64-bit)", elfInspection.Architecture);
}

static void OfficeMeasurementUsesCombinedBinaryInspection()
{
    if (!OperatingSystem.IsLinux())
    {
        return;
    }

    var previousPath = Environment.GetEnvironmentVariable("PATH");
    var directory = Path.Combine(Path.GetTempPath(), "paperbatch-office-binary-inspection-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var candidatePath = Path.Combine(directory, "wps.exe");
    File.WriteAllBytes(candidatePath, CreatePeImage("FPDF_InitLibrary"));
    try
    {
        Environment.SetEnvironmentVariable("PATH", directory);
        var result = OfficeProbe.Collect();
        var measurement = ((OfficeSuiteMeasurement[])result.InstalledSuites.Value!)
            .Single(item => item.Component == "wps.exe");

        Assert.Equal(EvidenceStatus.Verified, measurement.Bitness.Status);
        Assert.Equal("x64 (64-bit)", measurement.Bitness.Value);
        Assert.Equal(EvidenceStatus.Verified, measurement.BinarySha256.Status);
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", previousPath);
        Directory.Delete(directory, recursive: true);
    }
}

static void PdfiumMetadataPathRemainsBoundToOpenedFile()
{
    if (!OperatingSystem.IsLinux())
    {
        return;
    }

    var directory = Path.Combine(Path.GetTempPath(), "paperbatch-pdfium-metadata-lock-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "pdfium.dll");
    var originalPath = Path.Combine(directory, "original.dll");
    var replacementPath = Path.Combine(directory, "replacement.dll");
    File.WriteAllText(originalPath, "original PDFium image");
    File.WriteAllText(replacementPath, "replacement PDFium image");
    File.CreateSymbolicLink(path, originalPath);
    try
    {
        using var lockedFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var metadataPath = PdfiumProbe.GetMetadataPathForLockedFile(path, lockedFile.SafeFileHandle);
        File.Delete(path);
        File.CreateSymbolicLink(path, replacementPath);

        Assert.Equal("original PDFium image", File.ReadAllText(metadataPath));
        Assert.Equal(Path.GetFullPath(originalPath), LockedFilePath.ResolveTargetPath(path, lockedFile.SafeFileHandle));
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void PdfiumEvidenceReportsRequestedAndOpenedPaths()
{
    if (!OperatingSystem.IsLinux())
    {
        return;
    }

    var root = Path.Combine(Path.GetTempPath(), "paperbatch-pdfium-evidence-path-" + Guid.NewGuid().ToString("N"));
    var targetDirectory = Path.Combine(root, "target");
    var aliasDirectory = Path.Combine(root, "alias");
    Directory.CreateDirectory(targetDirectory);
    Directory.CreateDirectory(aliasDirectory);
    var targetPath = Path.Combine(targetDirectory, "pdfium.dll");
    var requestedPath = Path.Combine(aliasDirectory, "pdfium.dll");
    File.WriteAllBytes(targetPath, CreatePeImage("FPDF_InitLibrary"));
    File.CreateSymbolicLink(requestedPath, targetPath);
    try
    {
        var result = PdfiumProbe.Collect(requestedPath);
        Assert.Equal(EvidenceStatus.Verified, result.Identity.Status);
        var identityJson = ProbeJson.Serialize(result.Identity.Value!);
        Assert.Contains(requestedPath, identityJson);
        Assert.Contains(targetPath, identityJson);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void LibreOfficeCandidateRequiresPinnedExecutableDigest()
{
    if (!OperatingSystem.IsLinux())
    {
        return;
    }

    var previousPath = Environment.GetEnvironmentVariable("PATH");
    var root = Path.Combine(Path.GetTempPath(), "paperbatch-office-pin-" + Guid.NewGuid().ToString("N"));
    var directory = Path.Combine(root, "program");
    Directory.CreateDirectory(directory);
    var launcherPath = Path.Combine(directory, "soffice");
    var launcherMarkerPath = Path.Combine(root, "launcher-launched");
    var enginePath = Path.Combine(directory, "soffice.bin");
    var engineMarkerPath = Path.Combine(root, "engine-launched");
    CreateShellExecutable(launcherPath, "LibreOffice 1.2.3", launcherMarkerPath);
    CreateShellExecutable(enginePath, "LibreOffice 1.2.3", engineMarkerPath);
    try
    {
        Environment.SetEnvironmentVariable("PATH", directory);
        var unpinned = OfficeProbe.Collect();
        Assert.Equal(EvidenceStatus.Blocked, unpinned.Exports.LibreOffice.Status);
        Assert.Equal(EvidenceStatus.Blocked, unpinned.Coexistence.LibreOffice.Status);
        Assert.True(!File.Exists(launcherMarkerPath));
        Assert.True(!File.Exists(engineMarkerPath));

        var mismatched = OfficeProbe.Collect(
            expectedLibreOfficeEngineSha256: new string('0', 64),
            libreOfficeEnginePath: enginePath);
        Assert.Equal(EvidenceStatus.Blocked, mismatched.Exports.LibreOffice.Status);
        Assert.Equal(EvidenceStatus.Blocked, mismatched.Coexistence.LibreOffice.Status);
        Assert.True(!File.Exists(launcherMarkerPath));
        Assert.True(!File.Exists(engineMarkerPath));

        var pinned = OfficeProbe.Collect(
            expectedLibreOfficeEngineSha256: Sha256File(enginePath),
            libreOfficeEnginePath: enginePath);
        Assert.Equal(EvidenceStatus.NotRun, pinned.Exports.LibreOffice.Status);
        Assert.Equal(EvidenceStatus.NotRun, pinned.Coexistence.LibreOffice.Status);
        Assert.True(!File.Exists(launcherMarkerPath));
        Assert.True(File.Exists(engineMarkerPath));
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", previousPath);
        Directory.Delete(root, recursive: true);
    }
}

static void LibreOfficeLaunchRemainsBoundToPinnedFileHandle()
{
    if (!OperatingSystem.IsLinux())
    {
        return;
    }

    var directory = Path.Combine(Path.GetTempPath(), "paperbatch-pinned-lo-race-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var enginePath = Path.Combine(directory, "soffice.bin");
    var originalPath = Path.Combine(directory, "pinned-original.bin");
    var replacementPath = Path.Combine(directory, "replacement.bin");
    CreateShellExecutable(enginePath, "original-engine");
    CreateShellExecutable(replacementPath, "replacement-engine");
    try
    {
        using var engine = new FileStream(enginePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var expectedSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(engine)).ToLowerInvariant();
        Assert.Equal(expectedSha256, Sha256File(enginePath));
        using var snapshot = PinnedEngineSnapshot.Create(engine, enginePath, expectedSha256);

        File.Move(enginePath, originalPath);
        File.Move(replacementPath, enginePath);
        var startInfo = new ProcessStartInfo(snapshot.LaunchPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(startInfo);
        Assert.True(process is not null);
        var output = process!.StandardOutput.ReadToEnd().Trim();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.Equal(0, process.ExitCode);
        Assert.Equal("original-engine", output);
        Assert.Equal(string.Empty, error);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void WindowsPinnedLaunchUsesHandleResolvedPath()
{
    if (!OperatingSystem.IsWindows())
    {
        return;
    }

    var directory = Path.Combine(Path.GetTempPath(), "paperbatch-pinned-lo-windows-path-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var enginePath = Path.Combine(directory, "soffice.bin");
    File.WriteAllText(enginePath, "pinned engine image");
    try
    {
        using var engine = new FileStream(enginePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var expectedSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(engine)).ToLowerInvariant();
        using var snapshot = PinnedEngineSnapshot.Create(engine, enginePath, expectedSha256);

        Assert.Equal(LockedFilePath.Resolve(enginePath, engine.SafeFileHandle), snapshot.LaunchPath);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void LinuxLibreOfficeLaunchClearsLoaderOverrides()
{
    if (!OperatingSystem.IsLinux())
    {
        return;
    }

    var directory = Path.Combine(Path.GetTempPath(), "paperbatch-lo-loader-env-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var startInfo = new ProcessStartInfo();
        startInfo.Environment["LD_PRELOAD"] = "/tmp/untrusted-preload.so";
        startInfo.Environment["LD_AUDIT"] = "/tmp/untrusted-audit.so";
        startInfo.Environment["LD_DEBUG"] = "all";
        startInfo.Environment["LD_LIBRARY_PATH"] = "/tmp/untrusted-libraries";
        startInfo.Environment["URE_BOOTSTRAP"] = "file:///tmp/untrusted-fundamentalrc";

        PinnedEngineSnapshot.ConfigureLinuxLaunchEnvironment(startInfo.Environment, directory);

        Assert.Equal(directory, startInfo.Environment["LD_LIBRARY_PATH"]);
        Assert.True(!startInfo.Environment.ContainsKey("LD_PRELOAD"));
        Assert.True(!startInfo.Environment.ContainsKey("LD_AUDIT"));
        Assert.True(!startInfo.Environment.ContainsKey("LD_DEBUG"));
        Assert.True(!startInfo.Environment.ContainsKey("URE_BOOTSTRAP"));
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void LinuxLibreOfficeAliasUsesTargetRuntimeDirectory()
{
    if (!OperatingSystem.IsLinux())
    {
        return;
    }

    var previousPath = Environment.GetEnvironmentVariable("PATH");
    var root = Path.Combine(Path.GetTempPath(), "paperbatch-lo-alias-runtime-" + Guid.NewGuid().ToString("N"));
    var targetDirectory = Path.Combine(root, "installation", "program");
    var aliasDirectory = Path.Combine(root, "alias");
    Directory.CreateDirectory(targetDirectory);
    Directory.CreateDirectory(aliasDirectory);
    var enginePath = Path.Combine(targetDirectory, "soffice.bin");
    var aliasPath = Path.Combine(aliasDirectory, "soffice.bin");
    var fundamentalPath = Path.Combine(targetDirectory, "fundamentalrc");
    File.WriteAllText(fundamentalPath, "target configuration");
    File.WriteAllText(Path.Combine(aliasDirectory, "fundamentalrc"), "alias configuration");
    File.WriteAllText(
        enginePath,
        "#!/bin/sh\nIFS= read -r runtime < \"$LD_LIBRARY_PATH/fundamentalrc\"\nprintf 'LibreOfficeDev 26.8.0.0.alpha0 cwd=%s loader=%s bootstrap=%s runtime=%s\\n' \"$PWD\" \"$LD_LIBRARY_PATH\" \"$URE_BOOTSTRAP\" \"$runtime\"\n");
    File.SetUnixFileMode(enginePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    File.CreateSymbolicLink(aliasPath, enginePath);
    try
    {
        Environment.SetEnvironmentVariable("PATH", aliasDirectory);
        var result = OfficeProbe.Collect(
            expectedLibreOfficeEngineSha256: Sha256File(enginePath),
            libreOfficeEnginePath: aliasPath);

        Assert.Equal(EvidenceStatus.NotRun, result.Exports.LibreOffice.Status);
        Assert.Equal(EvidenceStatus.NotRun, result.Coexistence.LibreOffice.Status);
        var measurements = (OfficeSuiteMeasurement[])result.InstalledSuites.Value!;
        var libreOffice = measurements.Single(item => item.Component == "Direct engine binary");
        var versionJson = ProbeJson.Serialize(libreOffice.Version.Value!);
        if (!versionJson.Contains($"cwd={targetDirectory}", StringComparison.Ordinal)
            || !versionJson.Contains("runtime=target configuration", StringComparison.Ordinal)
            || versionJson.Contains(aliasDirectory, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Pinned version evidence did not use the opened target directory: {versionJson}");
        }
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", previousPath);
        Directory.Delete(root, recursive: true);
    }
}

static void LibreOfficeMeasurementReportsRequestedAndOpenedPaths()
{
    if (!OperatingSystem.IsLinux())
    {
        return;
    }

    var previousPath = Environment.GetEnvironmentVariable("PATH");
    var root = Path.Combine(Path.GetTempPath(), "paperbatch-lo-evidence-path-" + Guid.NewGuid().ToString("N"));
    var targetDirectory = Path.Combine(root, "installation", "program");
    var aliasDirectory = Path.Combine(root, "alias");
    Directory.CreateDirectory(targetDirectory);
    Directory.CreateDirectory(aliasDirectory);
    var targetPath = Path.Combine(targetDirectory, "soffice.bin");
    var requestedPath = Path.Combine(aliasDirectory, "soffice.bin");
    File.WriteAllText(targetPath, "#!/bin/sh\nprintf 'LibreOfficeDev 26.8.0.0.alpha0\\n'\n");
    File.SetUnixFileMode(targetPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    File.CreateSymbolicLink(requestedPath, targetPath);
    try
    {
        Environment.SetEnvironmentVariable("PATH", aliasDirectory);
        var result = OfficeProbe.Collect(
            expectedLibreOfficeEngineSha256: Sha256File(targetPath),
            libreOfficeEnginePath: requestedPath);
        var measurement = ((OfficeSuiteMeasurement[])result.InstalledSuites.Value!)
            .Single(item => item.Component == "Direct engine binary");
        var measurementJson = ProbeJson.Serialize(measurement);
        var identityJson = ProbeJson.Serialize(measurement.BinarySha256.Value!);
        var versionJson = ProbeJson.Serialize(measurement.Version.Value!);
        Assert.Contains(requestedPath, measurementJson);
        Assert.Contains(targetPath, measurementJson);
        Assert.Contains(requestedPath, identityJson);
        Assert.Contains(targetPath, identityJson);
        Assert.Contains("runtimeComponentsSha256", versionJson);
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", previousPath);
        Directory.Delete(root, recursive: true);
    }
}

static void LibreOfficeIdentityBlocksSourceMutationAfterSnapshot()
{
    if (!OperatingSystem.IsLinux())
    {
        return;
    }

    var previousPath = Environment.GetEnvironmentVariable("PATH");
    var previousMutationPath = Environment.GetEnvironmentVariable("PAPERBATCH_TEST_ENGINE_MUTATION_PATH");
    var directory = Path.Combine(Path.GetTempPath(), "paperbatch-lo-identity-mutation-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var enginePath = Path.Combine(directory, "soffice.bin");
    File.WriteAllText(
        enginePath,
        "#!/bin/sh\nprintf '# source changed\\n' >> \"$PAPERBATCH_TEST_ENGINE_MUTATION_PATH\"\nprintf 'LibreOfficeDev 26.8.0.0.alpha0\\n'\n");
    File.SetUnixFileMode(enginePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    try
    {
        Environment.SetEnvironmentVariable("PATH", directory);
        Environment.SetEnvironmentVariable("PAPERBATCH_TEST_ENGINE_MUTATION_PATH", enginePath);
        var result = OfficeProbe.Collect(
            expectedLibreOfficeEngineSha256: Sha256File(enginePath),
            libreOfficeEnginePath: enginePath);
        var measurement = ((OfficeSuiteMeasurement[])result.InstalledSuites.Value!)
            .Single(item => item.RequestedExecutablePath == enginePath);

        Assert.Equal(EvidenceStatus.Verified, measurement.Version.Status);
        Assert.Equal(EvidenceStatus.Blocked, measurement.BinarySha256.Status);
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", previousPath);
        Environment.SetEnvironmentVariable("PAPERBATCH_TEST_ENGINE_MUTATION_PATH", previousMutationPath);
        Directory.Delete(directory, recursive: true);
    }
}

static void LibreOfficeIdentityBlocksOversizedSourceGrowthAfterVersion()
{
    if (!OperatingSystem.IsLinux())
    {
        return;
    }

    const long maximumBytes = 512;
    var previousPath = Environment.GetEnvironmentVariable("PATH");
    var previousMutationPath = Environment.GetEnvironmentVariable("PAPERBATCH_TEST_ENGINE_MUTATION_PATH");
    var directory = Path.Combine(Path.GetTempPath(), "paperbatch-lo-identity-size-race-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var enginePath = Path.Combine(directory, "soffice.bin");
    File.WriteAllText(enginePath, """
        #!/bin/sh
        i=0
        while [ "$i" -lt 20 ]; do
            printf 'xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\n' >> "$PAPERBATCH_TEST_ENGINE_MUTATION_PATH"
            i=$((i + 1))
        done
        printf 'LibreOfficeDev 26.8.0.0.alpha0\n'
        """);
    File.SetUnixFileMode(enginePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    try
    {
        Environment.SetEnvironmentVariable("PATH", directory);
        Environment.SetEnvironmentVariable("PAPERBATCH_TEST_ENGINE_MUTATION_PATH", enginePath);
        var result = OfficeProbe.Collect(
            expectedLibreOfficeEngineSha256: Sha256File(enginePath),
            libreOfficeEnginePath: enginePath,
            maximumBinaryBytes: maximumBytes);
        var measurement = ((OfficeSuiteMeasurement[])result.InstalledSuites.Value!)
            .Single(item => item.Component == "Direct engine binary");

        Assert.True(new FileInfo(enginePath).Length > maximumBytes);
        Assert.Equal(EvidenceStatus.Verified, measurement.Version.Status);
        Assert.Equal(EvidenceStatus.Blocked, measurement.Bitness.Status);
        Assert.Equal(EvidenceStatus.Blocked, measurement.BinarySha256.Status);
        Assert.Contains("inspection limit", measurement.BinarySha256.Limitation!);
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", previousPath);
        Environment.SetEnvironmentVariable("PAPERBATCH_TEST_ENGINE_MUTATION_PATH", previousMutationPath);
        Directory.Delete(directory, recursive: true);
    }
}

static void EngineSourceHashRejectsOversizedInput()
{
    var directory = Path.Combine(Path.GetTempPath(), "paperbatch-engine-hash-limit-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var enginePath = Path.Combine(directory, "soffice.bin");
    File.WriteAllBytes(enginePath, new byte[9]);
    try
    {
        using var engine = new FileStream(enginePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        engine.Position = 4;

        Assert.Throws<InvalidDataException>(() => PinnedEngineSnapshot.HashSourceWithinLimit(engine, maximumBytes: 8));
        Assert.Equal(0, engine.Position);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void OfficeAndWpsCandidateHashRejectsOversizedInput()
{
    var previousPath = Environment.GetEnvironmentVariable("PATH");
    var directory = Path.Combine(Path.GetTempPath(), "paperbatch-office-hash-limit-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var officePath = Path.Combine(directory, "WINWORD.EXE");
    var wpsPath = Path.Combine(directory, "wps.exe");
    File.WriteAllBytes(officePath, new byte[9]);
    File.WriteAllBytes(wpsPath, new byte[9]);
    try
    {
        Environment.SetEnvironmentVariable("PATH", directory);
        var result = OfficeProbe.Collect(
            expectedLibreOfficeEngineSha256: null,
            libreOfficeEnginePath: null,
            maximumBinaryBytes: 8);
        var measurements = (OfficeSuiteMeasurement[])result.InstalledSuites.Value!;

        foreach (var path in new[] { officePath, wpsPath })
        {
            var measurement = measurements.Single(item => item.RequestedExecutablePath == path);
            Assert.Equal(EvidenceStatus.Blocked, measurement.Bitness.Status);
            Assert.Equal(EvidenceStatus.Blocked, measurement.BinarySha256.Status);
            Assert.Contains("inspection limit", measurement.BinarySha256.Limitation!);
        }
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", previousPath);
        Directory.Delete(directory, recursive: true);
    }
}

static void LibreOfficeSnapshotSurvivesSourceMutation()
{
    if (!OperatingSystem.IsLinux())
    {
        return;
    }

    var directory = Path.Combine(Path.GetTempPath(), "paperbatch-pinned-lo-mutation-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var enginePath = Path.Combine(directory, "soffice.bin");
    CreateShellExecutable(enginePath, "original-engine");
    try
    {
        using var engine = new FileStream(enginePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var expectedSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(engine)).ToLowerInvariant();
        using var snapshot = PinnedEngineSnapshot.Create(engine, enginePath, expectedSha256);

        CreateShellExecutable(enginePath, "modified-engine");
        var startInfo = new ProcessStartInfo(snapshot.LaunchPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(startInfo);
        Assert.True(process is not null);
        var output = process!.StandardOutput.ReadToEnd().Trim();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.Equal(0, process.ExitCode);
        Assert.Equal("original-engine", output);
        Assert.Equal(string.Empty, error);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void LinuxEngineSnapshotRejectsOversizedSource()
{
    if (!OperatingSystem.IsLinux())
    {
        return;
    }

    var directory = Path.Combine(Path.GetTempPath(), "paperbatch-engine-size-limit-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var enginePath = Path.Combine(directory, "soffice.bin");
    var movedEnginePath = Path.Combine(directory, "opened-engine.bin");
    CreateShellExecutable(enginePath, "small engine image");
    try
    {
        using var engine = new FileStream(enginePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var expectedSha256 = Sha256File(enginePath);
        File.Move(enginePath, movedEnginePath);

        Assert.Throws<InvalidDataException>(() => PinnedEngineSnapshot.Create(
            engine,
            enginePath,
            expectedSha256,
            maximumSnapshotBytes: 8));
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void LinuxLibreOfficeRuntimeDirectoryRemainsPinned()
{
    if (!OperatingSystem.IsLinux())
    {
        return;
    }

    var root = Path.Combine(Path.GetTempPath(), "paperbatch-runtime-directory-pin-" + Guid.NewGuid().ToString("N"));
    var programDirectory = Path.Combine(root, "program");
    var movedDirectory = Path.Combine(root, "program-original");
    Directory.CreateDirectory(programDirectory);
    var enginePath = Path.Combine(programDirectory, "soffice.bin");
    File.WriteAllText(enginePath, "#!/bin/sh\nread runtime < \"$LD_LIBRARY_PATH/fundamentalrc\"\nprintf '%s\\n' \"$runtime\"\n");
    File.SetUnixFileMode(enginePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    File.WriteAllText(Path.Combine(programDirectory, "fundamentalrc"), "original runtime");

    try
    {
        var expectedSha256 = Sha256File(enginePath);
        using var engine = LockedFilePath.OpenReadOnlyRegularFile(enginePath, FileShare.Read);
        using var snapshot = PinnedEngineSnapshot.Create(engine, enginePath, expectedSha256);

        Directory.Move(programDirectory, movedDirectory);
        Directory.CreateDirectory(programDirectory);
        File.WriteAllText(Path.Combine(programDirectory, "fundamentalrc"), "replacement runtime");

        var startInfo = new ProcessStartInfo(snapshot.LaunchPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = snapshot.RuntimeDirectoryPath
        };
        PinnedEngineSnapshot.ConfigureLinuxLaunchEnvironment(startInfo.Environment, snapshot.RuntimeDirectoryPath);
        using var process = Process.Start(startInfo);
        Assert.True(process is not null);
        var output = process!.StandardOutput.ReadToEnd().Trim();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.Equal(0, process.ExitCode);
        Assert.Equal("original runtime", output);
        Assert.Equal(string.Empty, error);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void LibreOfficeRuntimeComponentMutationBlocksVersionEvidence()
{
    if (!OperatingSystem.IsLinux())
    {
        return;
    }

    var previousPath = Environment.GetEnvironmentVariable("PATH");
    var directory = Path.Combine(Path.GetTempPath(), "paperbatch-lo-runtime-component-mutation-" + Guid.NewGuid().ToString("N"));
    var programDirectory = Path.Combine(directory, "program");
    Directory.CreateDirectory(programDirectory);
    var enginePath = Path.Combine(programDirectory, "soffice.bin");
    File.WriteAllText(Path.Combine(programDirectory, "fundamentalrc"), "original runtime");
    File.WriteAllText(
        enginePath,
        "#!/bin/sh\nprintf 'changed runtime\\n' > \"$LD_LIBRARY_PATH/fundamentalrc\"\nprintf 'LibreOfficeDev 26.8.0.0.alpha0\\n'\n");
    File.SetUnixFileMode(enginePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    try
    {
        Environment.SetEnvironmentVariable("PATH", programDirectory);
        var result = OfficeProbe.Collect(
            expectedLibreOfficeEngineSha256: Sha256File(enginePath),
            libreOfficeEnginePath: enginePath);
        var measurement = ((OfficeSuiteMeasurement[])result.InstalledSuites.Value!)
            .Single(item => item.RequestedExecutablePath == enginePath);

        Assert.Equal(EvidenceStatus.Blocked, measurement.Version.Status);
        Assert.Contains("runtime", measurement.Version.Limitation!.ToLowerInvariant());
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", previousPath);
        Directory.Delete(directory, recursive: true);
    }
}

static void RuntimeManifestRejectsReplacedEngineEntryTypes()
{
    if (!OperatingSystem.IsLinux())
    {
        return;
    }

    foreach (var replacement in new[] { "symlink", "fifo", "regular" })
    {
        var root = Path.Combine(Path.GetTempPath(), "paperbatch-runtime-engine-entry-" + Guid.NewGuid().ToString("N"));
        var programDirectory = Path.Combine(root, "program");
        Directory.CreateDirectory(programDirectory);
        var enginePath = Path.Combine(programDirectory, "soffice.bin");
        File.WriteAllText(enginePath, "pinned engine image");

        try
        {
            var expectedSha256 = Sha256File(enginePath);
            using var engine = LockedFilePath.OpenReadOnlyRegularFile(enginePath, FileShare.Read);
            using var snapshot = PinnedEngineSnapshot.Create(engine, enginePath, expectedSha256);

            File.Delete(enginePath);
            if (replacement == "symlink")
            {
                var targetPath = Path.Combine(root, "external-engine");
                File.WriteAllText(targetPath, "replacement engine");
                File.CreateSymbolicLink(enginePath, targetPath);
            }
            else if (replacement == "fifo")
            {
                using var process = Process.Start(new ProcessStartInfo("mkfifo")
                {
                    UseShellExecute = false,
                    ArgumentList = { enginePath }
                });
                Assert.True(process is not null);
                process!.WaitForExit();
                Assert.Equal(0, process.ExitCode);
            }
            else
            {
                File.WriteAllText(enginePath, "replacement engine image");
            }

            if (replacement == "regular")
            {
                Assert.Throws<InvalidDataException>(snapshot.VerifyRuntimeComponentsUnchanged);
            }
            else
            {
                Assert.Throws<IOException>(snapshot.VerifyRuntimeComponentsUnchanged);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void RuntimeEntryLimitStopsBeforeMaterializingDirectory()
{
    var entriesEnumerated = 0;
    var entriesSeen = 0;
    IEnumerable<string> Entries()
    {
        for (var index = 0; index < 12; index++)
        {
            entriesEnumerated++;
            yield return $"component-{index:D2}.so";
        }
    }

    Assert.Throws<IOException>(() => PinnedEngineSnapshot.EnumerateRuntimeEntries(
        Entries(),
        ref entriesSeen,
        maximumEntries: 2));
    Assert.Equal(3, entriesEnumerated);
}

static void LinuxRuntimeDirectoryResolutionRejectsSymlinkReplacement()
{
    if (!OperatingSystem.IsLinux())
    {
        return;
    }

    var root = Path.Combine(Path.GetTempPath(), "paperbatch-runtime-symlink-race-" + Guid.NewGuid().ToString("N"));
    var programDirectory = Path.Combine(root, "program");
    var movedDirectory = Path.Combine(root, "program-original");
    Directory.CreateDirectory(programDirectory);
    var enginePath = Path.Combine(programDirectory, "soffice.bin");
    File.WriteAllText(enginePath, "pinned engine image");

    try
    {
        using var engine = LockedFilePath.OpenReadOnlyRegularFile(enginePath, FileShare.Read);
        var openedEnginePath = LockedFilePath.ResolveTargetPath(enginePath, engine.SafeFileHandle);
        Directory.Move(programDirectory, movedDirectory);
        Directory.CreateDirectory(programDirectory);
        File.CreateSymbolicLink(Path.Combine(programDirectory, "soffice.bin"), Path.Combine(movedDirectory, "soffice.bin"));

        Assert.Throws<IOException>(() =>
        {
            using var runtimeDirectory = LockedFilePath.OpenLinuxDirectoryForOpenedFile(openedEnginePath, engine.SafeFileHandle);
        });
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void LinuxRuntimeDirectoryResolutionRejectsHardlinkReplacement()
{
    if (!OperatingSystem.IsLinux())
    {
        return;
    }

    var root = Path.Combine(Path.GetTempPath(), "paperbatch-runtime-hardlink-race-" + Guid.NewGuid().ToString("N"));
    var programDirectory = Path.Combine(root, "program");
    var movedDirectory = Path.Combine(root, "program-original");
    Directory.CreateDirectory(programDirectory);
    var enginePath = Path.Combine(programDirectory, "soffice.bin");
    File.WriteAllText(enginePath, "pinned engine image");

    try
    {
        using var engine = LockedFilePath.OpenReadOnlyRegularFile(enginePath, FileShare.Read);
        var openedEnginePath = LockedFilePath.ResolveTargetPath(enginePath, engine.SafeFileHandle);
        Directory.Move(programDirectory, movedDirectory);
        Directory.CreateDirectory(programDirectory);
        using var link = Process.Start(new ProcessStartInfo("ln")
        {
            UseShellExecute = false,
            ArgumentList = { Path.Combine(movedDirectory, "soffice.bin"), Path.Combine(programDirectory, "soffice.bin") }
        });
        Assert.True(link is not null);
        link!.WaitForExit();
        Assert.Equal(0, link.ExitCode);

        Assert.Throws<IOException>(() =>
        {
            using var runtimeDirectory = LockedFilePath.OpenLinuxDirectoryForOpenedFile(openedEnginePath, engine.SafeFileHandle);
        });
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static string Sha256File(string path) => Convert.ToHexString(
    System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

static void UnrelatedSofficeCandidateCannotSatisfyLibreOfficeEvidence()
{
    if (OperatingSystem.IsWindows())
    {
        return;
    }

    var previousPath = Environment.GetEnvironmentVariable("PATH");
    var directory = Path.Combine(Path.GetTempPath(), "paperbatch-office-probe-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var launcherPath = Path.Combine(directory, "soffice");
    var markerPath = Path.Combine(directory, "launcher-launched");
    CreateShellExecutable(launcherPath, "Other Converter 3.0", markerPath);
    try
    {
        Environment.SetEnvironmentVariable("PATH", directory);
        var result = OfficeProbe.Collect();
        Assert.Equal(EvidenceStatus.Blocked, result.Exports.LibreOffice.Status);
        Assert.Equal(EvidenceStatus.Blocked, result.Coexistence.LibreOffice.Status);
        Assert.Contains("Unverified executable candidate", ProbeJson.Serialize(result.InstalledSuites));
        Assert.True(!File.Exists(markerPath));
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", previousPath);
        Directory.Delete(directory, recursive: true);
    }
}

static void CreateShellExecutable(string path, string versionOutput, string? markerPath = null)
{
    var markerCommand = markerPath is null ? string.Empty : $"printf 'launched' > '{markerPath}'\n";
    File.WriteAllText(path, $"#!/bin/sh\nprintf '%s\\n' '{versionOutput}'\n{markerCommand}");
    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

static void PrintSimulationIsDeterministic()
{
    var first = PrintJobSimulator.RunGdiJobIdScenario();
    var second = PrintJobSimulator.RunGdiJobIdScenario();

    Assert.True(first.SimulationOnly);
    Assert.Equal(EvidenceSource.Simulation, first.JobIdEvidence.Source);
    Assert.Equal(EvidenceStatus.Verified, first.JobIdEvidence.Status);
    Assert.Equal("SIM-0001", first.Steps[0].JobId);
    Assert.True(first.Steps.SequenceEqual(second.Steps));
}

static byte[] CreatePeImage(params string[] exports)
{
    const int peOffset = 0x80;
    const int optionalHeaderSize = 0xF0;
    const int sectionOffset = peOffset + 24 + optionalHeaderSize;
    const int rawSectionOffset = 0x200;
    const uint sectionRva = 0x1000;
    const uint exportRva = sectionRva;
    const uint functionTableRva = sectionRva + 0x40;
    const uint namesTableRva = sectionRva + 0x60;
    const uint ordinalsTableRva = sectionRva + 0x80;
    const uint firstStringRva = sectionRva + 0xA0;
    var image = new byte[rawSectionOffset + 0x400];

    image[0] = (byte)'M';
    image[1] = (byte)'Z';
    BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C, 4), peOffset);
    image[peOffset] = (byte)'P';
    image[peOffset + 1] = (byte)'E';
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 4, 2), 0x8664);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 6, 2), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 20, 2), optionalHeaderSize);

    var optionalOffset = peOffset + 24;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optionalOffset, 2), 0x20B);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 60, 4), rawSectionOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 108, 4), 16);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 112, 4), exportRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optionalOffset + 116, 4), 0x200);

    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 8, 4), 0x400);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 12, 4), sectionRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 16, 4), 0x400);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionOffset + 20, 4), rawSectionOffset);

    var exportOffset = rawSectionOffset;
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(exportOffset + 20, 4), (uint)exports.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(exportOffset + 24, 4), (uint)exports.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(exportOffset + 28, 4), functionTableRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(exportOffset + 32, 4), namesTableRva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(exportOffset + 36, 4), ordinalsTableRva);

    var stringOffset = rawSectionOffset + 0xA0;
    for (var index = 0; index < exports.Length; index++)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(
            image.AsSpan(rawSectionOffset + 0x60 + (index * 4), 4),
            firstStringRva + (uint)(stringOffset - (rawSectionOffset + 0xA0)));
        BinaryPrimitives.WriteUInt32LittleEndian(
            image.AsSpan(rawSectionOffset + 0x40 + (index * 4), 4),
            sectionRva + 0x300 + (uint)index);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(rawSectionOffset + 0x80 + (index * 2), 2), (ushort)index);
        var bytes = System.Text.Encoding.ASCII.GetBytes(exports[index] + "\0");
        bytes.CopyTo(image, stringOffset);
        stringOffset += bytes.Length;
    }

    return image;
}

static byte[] CreateElfImage(byte elfClass, ushort machine)
{
    var image = new byte[64];
    image[0] = 0x7F;
    image[1] = (byte)'E';
    image[2] = (byte)'L';
    image[3] = (byte)'F';
    image[4] = elfClass;
    image[5] = 1;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(18, 2), machine);
    return image;
}

static class Assert
{
    public static void True(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected true.");
        }
    }

    public static void False(bool value)
    {
        if (value)
        {
            throw new InvalidOperationException("Expected false.");
        }
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    public static void Contains<T>(T expected, IEnumerable<T> actual)
    {
        if (!actual.Contains(expected))
        {
            throw new InvalidOperationException($"Expected collection to contain '{expected}'.");
        }
    }

    public static void Contains(string expected, string actual)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected text to contain '{expected}'.");
        }
    }

    public static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}

static class TestNativeMethods
{
    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true, CharSet = CharSet.Ansi)]
    internal static extern int MkFifo(string path, uint mode);
}

sealed class RewritingSeekableStream(byte[] original, byte[] replacement) : Stream
{
    private byte[] _current = original;
    private long _position;

    public int Rewinds { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _current.LongLength;

    public override long Position
    {
        get => _position;
        set
        {
            if (value == 0 && _position >= _current.LongLength && _position > 0)
            {
                _current = replacement;
                Rewinds++;
            }

            _position = value;
        }
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var available = (int)Math.Min(count, _current.LongLength - _position);
        if (available <= 0)
        {
            return 0;
        }

        Array.Copy(_current, _position, buffer, offset, available);
        _position += available;
        return available;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _current.LongLength + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        return _position;
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
