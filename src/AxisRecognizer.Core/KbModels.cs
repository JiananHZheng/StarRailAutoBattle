namespace AxisRecognizer;

public enum UnitKind
{
    Character,
    CharacterSummon,
    Monster,
    MonsterSummon,
    /// <summary>An empty action-axis slot; its samples teach the tool what "no avatar" looks like.</summary>
    Empty,

    /// <summary>
    /// An axis entry that is not a unit's action (e.g. a "special moment" reminder). It is recognized so it can be
    /// left out: it never appears in results and never counts as an action.
    /// </summary>
    Marker,

    /// <summary>
    /// A cell that belongs to the entry right above it (e.g. the team-portrait cell of 阿哈时刻's extra action).
    /// The two count as one action; results fold it into the line above.
    /// </summary>
    Attachment,

    /// <summary>Kind not given yet (units created from new avatars); the user adds it later as a note.</summary>
    Unclassified,
}

public enum SlotKind
{
    /// <summary>The top-left cell: the action happening now.</summary>
    Current,
    /// <summary>Main axis below the current cell, top to bottom.</summary>
    Main,
    /// <summary>Pending inserted actions to the right of the current cell, left to right.</summary>
    Insert,
}

public sealed class UnitRecord
{
    /// <summary>Global id "U0001" (one per distinct avatar); "EMPTY" for the built-in empty slot.</summary>
    public required string Id { get; set; }

    /// <summary>Earlier ids of this unit (e.g. CH-爻光, M0003); they still resolve to it.</summary>
    public List<string> Aliases { get; init; } = [];

    /// <summary>May be corrected later (e.g. monster → monster summon); the id stays the same.</summary>
    public required UnitKind Kind { get; set; }

    public string? Name { get; set; }

    /// <summary>"confirmed" when the name was verified, "candidate" when unverified, "none" when no name is known.</summary>
    public string NameStatus { get; set; } = "none";

    public string? NameSource { get; set; }
    public string? Notes { get; set; }

    /// <summary>Set when the user confirmed this id is the same unit as another; samples were moved there.</summary>
    public string? MergedInto { get; set; }

    /// <summary>
    /// The unit's actions are inserted turns wherever its cell sits (e.g. 阿哈时刻), so entering the current cell
    /// numbers it as "main-insert" instead of a new main-axis action.
    /// </summary>
    public bool ActsAsInsert { get; set; }

    /// <summary>
    /// 临时: created by the tool from a run's new avatars without the user's check. It takes part in recognition (so
    /// the same unit keeps one id across runs) and is shown as "（临时）" until the user confirms it.
    /// </summary>
    public bool Provisional { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public List<string> History { get; init; } = [];
}

public sealed class SourceInfo
{
    /// <summary>user-screenshot, user-video, local-capture, web (links only unless the license allows saving), synthetic.</summary>
    public required string Type { get; init; }

    /// <summary>Original file name, or video file name plus timestamp.</summary>
    public required string Origin { get; init; }

    public string? Url { get; init; }
    public string? License { get; init; }
    public DateTimeOffset AccessedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>A web page or video used as a reference without saving its images (license unconfirmed).</summary>
public sealed class WebLinkRecord
{
    public required string Url { get; init; }
    public string? Title { get; init; }
    public string? Uploader { get; init; }

    /// <summary>The page's own reuse notice, quoted (e.g. 未经作者授权，禁止转载).</summary>
    public string? Notice { get; init; }

    public DateTimeOffset AccessedAt { get; init; } = DateTimeOffset.Now;
    public string? Notes { get; init; }
}

public sealed class ScreenshotRecord
{
    public required string Id { get; init; }

    /// <summary>PNG for screenshots; JPEG for frames taken from videos (already lossy, and about a sixth of the size).</summary>
    public required string File { get; set; }

    public required string Sha256 { get; set; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required SourceInfo Source { get; init; }
    public string? GameVersion { get; set; }

    /// <summary>"train" screenshots feed samples; "holdout" screenshots are only used to evaluate.</summary>
    public string Split { get; set; } = "train";

    public int AddedInKbVersion { get; init; }

    /// <summary>Ground truth per slot name (unit id or EMPTY), when labeled.</summary>
    public Dictionary<string, string> Truth { get; init; } = [];

    public string? Notes { get; set; }
}

public sealed class SampleRecord
{
    public required string Id { get; init; }
    public required string UnitId { get; set; }
    public required string ScreenshotId { get; init; }
    public required string Slot { get; init; }
    public required RelRect Rect { get; init; }
    public required string File { get; set; }
    public required string Sha256 { get; init; }

    /// <summary>user (told by the user), claude-assisted (labeled during KB building with Claude's help, needs user confirmation).</summary>
    public required string LabelSource { get; init; }

    public string Status { get; set; } = "unconfirmed";
    public int AddedInKbVersion { get; init; }
    public string? Notes { get; set; }

    /// <summary>Portrait region inside the stored crop (fractions of the crop); null = whole crop.</summary>
    public RelRect? Feature { get; init; }

    /// <summary>Areas of the portrait region left out of comparison (fractions of that region).</summary>
    public List<RelRect>? Ignore { get; init; }
}

public sealed class SlotDef
{
    public required string Name { get; init; }
    public required SlotKind Kind { get; init; }
    public required int Order { get; init; }
    public required RelRect Rect { get; init; }
}

public sealed class LayoutProfile
{
    public required string Id { get; init; }
    public required double AspectMin { get; init; }
    public required double AspectMax { get; init; }
    /// <summary>Fixed slots (e.g. insert slots). With <see cref="Axis"/> set, current and main cells are detected instead.</summary>
    public required List<SlotDef> Slots { get; init; }

    /// <summary>When set, the current cell and main-axis cells are found by frame detection.</summary>
    public AxisColumn? Axis { get; set; }

    public List<string> CalibratedFrom { get; init; } = [];
    public string? Notes { get; set; }
}

public sealed class RecognitionSettings
{
    public int FeatureSize { get; set; } = 48;

    /// <summary>Fraction of the crop trimmed on each side before comparing, to drop the slot border and background.</summary>
    public double Inset { get; set; } = 0.10;

    public double WeightGray { get; set; } = 0.4;
    public double WeightEdge { get; set; } = 0.3;
    public double WeightHist { get; set; } = 0.3;

    /// <summary>Minimum score for a confident answer.</summary>
    public double AcceptScore { get; set; } = 0.80;

    /// <summary>Minimum gap between the best and second-best unit for a confident answer.</summary>
    public double AcceptMargin { get; set; } = 0.03;

    /// <summary>Candidates below this score are not listed.</summary>
    public double CandidateScore { get; set; } = 0.50;

    /// <summary>Search around each layout slot: offset as a fraction of slot size, and scale steps.</summary>
    public double RefineOffset { get; set; } = 0.06;

    public double RefineScale { get; set; } = 0.06;

    /// <summary>Used only while the KB has no EMPTY samples: a crop whose gray std-dev is below this is treated as empty.</summary>
    public double EmptyStdFallback { get; set; } = 8.0;

    /// <summary>
    /// When &gt; 0, a cell whose best non-EMPTY score is below this is treated as empty. Off by default: it would
    /// also hide genuinely new units, so it must be tuned on real screenshots before being enabled.
    /// </summary>
    public double EmptyBelowScore { get; set; }

    /// <summary>Two samples of the same unit scoring above this are duplicates; the new one is not stored.</summary>
    public double DuplicateScore { get; set; } = 0.985;

    /// <summary>
    /// Within one run, an unknown avatar joins a group of new avatars at this similarity (temporary ids 新01…).
    /// Near misses down to 0.07 below it are reported as 疑似.
    /// </summary>
    public double GroupJoinScore { get; set; } = 0.72;
}

public sealed class KbMeta
{
    public int Version { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public string Game { get; init; } = "崩坏：星穹铁道";
    public string Scope { get; init; } = "3.0 版本至今的行动轴单位头像";
    public RecognitionSettings Settings { get; init; } = new();
}
