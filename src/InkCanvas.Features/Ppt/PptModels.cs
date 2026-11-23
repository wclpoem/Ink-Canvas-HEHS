using InkCanvas.Core.Ink;

namespace InkCanvas.Features.Ppt;

public enum PptConnectionState { Detached, Editing, SlideShow }
public enum PptSlideShowStart { PresentationSettings, FromBeginning, FromCurrentSlide }

/// <summary>Identity is connection-local COM identity, NOT a filename or slide count.</summary>
public sealed record PptPresentationInfo(
    string Identity, string Name, string FullName, bool HasSavedPath, int SlideCount,
    double SlideWidth, double SlideHeight, string ApplicationProgId);

/// <summary>SlideId survives reordering; Index is the one-based argument to GotoSlide.</summary>
public sealed record PptSlideInfo(
    int SlideId, int Index, string Name, bool? Hidden, bool? AdvanceOnTime, double? AdvanceTime);

/// <summary>
/// Managed-only snapshot, safe to retain without retaining Office RCWs. A null Slide means
/// Office is temporarily showing no slide (e.g. the end screen), not that the show ended.
/// ShowPosition is one-based within the show and can differ from Slide.Index in custom shows.
/// </summary>
public sealed record PptSnapshot
{
    public static PptSnapshot Detached { get; } = new();
    public PptConnectionState State { get; init; }
    public PptPresentationInfo Presentation { get; init; }
    public PptSlideInfo Slide { get; init; }
    public nint WindowHandle { get; init; }
    public int ShowPosition { get; init; }
    /// <summary>PowerPoint PpSlideShowState (1 running, 2 paused, 3 black, 4 white, 5 done).</summary>
    public int? ShowState { get; init; }
    /// <summary>Includes PowerPoint Reading View and other windowed slide shows.</summary>
    public bool IsWindowedShow { get; init; }
    public bool IsAttached => State != PptConnectionState.Detached;
    public bool IsSlideShowRunning => State == PptConnectionState.SlideShow;
}

public sealed record PptPresentationDetails(
    PptPresentationInfo Presentation, IReadOnlyList<PptSlideInfo> Slides, int? AdvanceMode)
{
    public bool HasHiddenSlides => Slides.Any(s => s.Hidden == true);
    // Matches the original detection, which deliberately did not check AdvanceMode.
    public bool HasSlideTimings => Slides.Any(s => s.AdvanceOnTime == true && s.AdvanceTime > 0);
}

public sealed record PptFailure(string Operation, string Message, int ErrorCode);
public enum PptInkTransitionKind { Begin, Slide, End }
public enum PptScreenshotReason { BeforeNavigation, ExternalSlideChange, Explicit }

/// <summary>All strokes are detached clones, not live controller or cache objects.</summary>
public sealed record PptInkTransition(
    PptInkTransitionKind Kind, PptSnapshot Previous, PptSnapshot Current,
    IReadOnlyList<InkStroke> Outgoing, IReadOnlyList<InkStroke> Incoming);

/// <summary>
/// Handle synchronously to capture the screen before the ink replacement/navigation.
/// SuggestedPath follows the original screenshot folder layout. The host owns pixel capture,
/// PNG encoding, directory creation, and optional standalone stroke saving.
/// </summary>
public sealed record PptScreenshotRequest(
    PptSnapshot Snapshot, PptSlideInfo InkSlide, IReadOnlyList<InkStroke> Strokes, PptScreenshotReason Reason,
    string SuggestedPath, bool SaveInkAlongside);

/// <summary>No action is taken automatically: the host displays and answers these notices.</summary>
public sealed record PptPresentationNotice(
    PptPresentationDetails Details, int? PreviousSlideIndex,
    bool OfferRestoreHiddenSlides, bool OfferDisableTimings, bool LegacyInkFilesDetected,
    bool ResumeIndexDegraded = false, bool OfferInkMappingReview = false,
    string InkMappingSummary = null, string BackupDirectory = null);

public sealed record PptStoredSlide(int SlideId, int SlideIndex, IReadOnlyList<InkStroke> Strokes,
    bool IsDeferred = false, int? StoredStrokeCount = null);

/// <summary>
/// Store implementations receive only managed snapshots, never COM objects.
/// LastSlideId is the durable resume key; LastSlideIndex is kept for display and as a
/// fallback for archives that predate slide-id resume. ManifestVersion is 1 for legacy
/// manifests (no Version field, nothing to migrate from) and 2 once migrated.
/// </summary>
public sealed record PptInkArchive(
    PptPresentationInfo Presentation, int? LastSlideIndex, int? LastShowPosition,
    IReadOnlyList<PptStoredSlide> Slides, bool LegacyInkFilesDetected = false,
    int? LastSlideId = null, int ManifestVersion = 1);

public interface IPptInkStore
{
    PptInkArchive Load(PptPresentationInfo presentation);
    void Save(PptInkArchive archive);
}
