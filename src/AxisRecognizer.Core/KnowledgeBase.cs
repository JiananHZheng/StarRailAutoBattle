using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCvSharp;

namespace AxisRecognizer;

/// <summary>
/// The on-disk action-axis avatar knowledge base. Layout:
/// <code>
/// kb/kb.json            version + recognition settings
/// kb/units.json         unit registry (stable ids)
/// kb/screenshots.json   original screenshots with source, license, split, truth labels
/// kb/samples.json       cropped avatar samples with labels and provenance
/// kb/layouts.json       action-axis layouts in relative coordinates
/// kb/changelog.md       one entry per change
/// kb/corrections.log    misrecognitions reported by the user
/// kb/screenshots/*.png, kb/samples/&lt;unit&gt;/*.png
/// </code>
/// </summary>
public sealed class KnowledgeBase
{
    public const string EmptyUnitId = "EMPTY";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private KnowledgeBase(string root)
    {
        Root = root;
    }

    public string Root { get; }
    public KbMeta Meta { get; private set; } = new();
    public List<UnitRecord> Units { get; private set; } = [];
    public List<ScreenshotRecord> Screenshots { get; private set; } = [];
    public List<SampleRecord> Samples { get; private set; } = [];
    public List<LayoutProfile> Layouts { get; private set; } = [];
    public List<WebLinkRecord> WebLinks { get; private set; } = [];

    public int Version => Meta.Version;
    public RecognitionSettings Settings => Meta.Settings;

    private string P(string name) => Path.Combine(Root, name);

    public static bool Exists(string root) => File.Exists(Path.Combine(root, "kb.json"));

    public static KnowledgeBase Create(string root)
    {
        if (Exists(root))
        {
            throw new InvalidOperationException($"知识库已存在：{root}");
        }

        Directory.CreateDirectory(root);
        var kb = new KnowledgeBase(root);
        kb.Units.Add(new UnitRecord
        {
            Id = EmptyUnitId,
            Kind = UnitKind.Empty,
            Name = "空格",
            NameStatus = "confirmed",
            NameSource = "内置",
            Notes = "行动轴空位；样本用于区分“没有头像”。",
        });
        kb.Commit("初始化知识库", ["创建空知识库与内置单位 EMPTY。"]);
        return kb;
    }

    public static KnowledgeBase Load(string root)
    {
        if (!Exists(root))
        {
            throw new InvalidOperationException($"找不到知识库：{root}（请先运行 axis kb init）");
        }

        var kb = new KnowledgeBase(root)
        {
            Meta = ReadJson<KbMeta>(Path.Combine(root, "kb.json")) ?? new KbMeta(),
            Units = ReadJson<List<UnitRecord>>(Path.Combine(root, "units.json")) ?? [],
            Screenshots = ReadJson<List<ScreenshotRecord>>(Path.Combine(root, "screenshots.json")) ?? [],
            Samples = ReadJson<List<SampleRecord>>(Path.Combine(root, "samples.json")) ?? [],
            Layouts = ReadJson<List<LayoutProfile>>(Path.Combine(root, "layouts.json")) ?? [],
            WebLinks = ReadJson<List<WebLinkRecord>>(Path.Combine(root, "web_links.json")) ?? [],
        };
        return kb;
    }

    private static T? ReadJson<T>(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) : default;

    private static void WriteJson<T>(string path, T value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Saves everything, bumps the version and appends a changelog entry.</summary>
    public void Commit(string title, IEnumerable<string> details)
    {
        Meta.Version++;
        WriteJson(P("kb.json"), Meta);
        WriteJson(P("units.json"), Units);
        WriteJson(P("screenshots.json"), Screenshots);
        WriteJson(P("samples.json"), Samples);
        WriteJson(P("layouts.json"), Layouts);
        WriteJson(P("web_links.json"), WebLinks);
        WriteUnitsText();

        var entry = new StringBuilder();
        entry.AppendLine($"## v{Meta.Version} — {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} — {title}");
        foreach (var line in details)
        {
            entry.AppendLine($"- {line}");
        }

        entry.AppendLine($"- 内容校验：{ContentHash()}");
        entry.AppendLine();
        File.AppendAllText(P("changelog.md"), entry.ToString(), new UTF8Encoding(false));
    }

    /// <summary>A short hash of all index files (sample files are covered through their sha256).</summary>
    public string ContentHash()
    {
        using var sha = SHA256.Create();
        var text = string.Join("\n",
            JsonSerializer.Serialize(Meta.Settings, JsonOptions),
            JsonSerializer.Serialize(Units, JsonOptions),
            JsonSerializer.Serialize(Samples, JsonOptions),
            JsonSerializer.Serialize(Layouts, JsonOptions));
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();
    }

    private void WriteUnitsText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 单位登记表（知识库 v{Meta.Version}，由 units.json 生成，请勿手改）");
        sb.AppendLine("# 编号\t类型\t名称\t名称状态\t样本数\t旧编号（别名）\t备注");
        foreach (var unit in Units.OrderBy(u => u.Id, StringComparer.Ordinal))
        {
            var count = Samples.Count(s => s.UnitId == unit.Id);
            var notes = unit.MergedInto is null ? unit.Notes : $"已合并到 {unit.MergedInto}。{unit.Notes}";
            var aliases = unit.Aliases.Count == 0 ? "-" : string.Join("、", unit.Aliases);
            var status = unit.Provisional ? "临时（未核对）" : unit.NameStatus;
            sb.AppendLine($"{unit.Id}\t{KindText(unit.Kind)}\t{unit.Name ?? "-"}\t{status}\t{count}\t{aliases}\t{notes}");
        }

        File.WriteAllText(P("units.txt"), sb.ToString(), new UTF8Encoding(false));
    }

    public static string KindText(UnitKind kind) => kind switch
    {
        UnitKind.Character => "角色",
        UnitKind.CharacterSummon => "角色召唤物",
        UnitKind.Monster => "怪物",
        UnitKind.MonsterSummon => "怪物召唤物",
        UnitKind.Marker => "非单位提醒条目",
        UnitKind.Attachment => "附属格（并入上一条目）",
        UnitKind.Unclassified => "未分类",
        _ => "空格",
    };

    /// <summary>True for entries that are never listed as actions (markers).</summary>
    public bool IsMarker(string? unitId) => unitId is not null && FindUnit(unitId)?.Kind == UnitKind.Marker;

    public bool IsAttachment(string? unitId) => unitId is not null && FindUnit(unitId)?.Kind == UnitKind.Attachment;

    public bool ActsAsInsert(string? unitId) => unitId is not null && FindUnit(unitId)?.ActsAsInsert == true;

    /// <summary>Cells that never count as an action of their own: markers and attachments.</summary>
    public bool IsNonAction(string? unitId) => IsMarker(unitId) || IsAttachment(unitId);

    // ---------- Units ----------

    /// <summary>By global id or by any earlier id (alias).</summary>
    public UnitRecord? FindUnit(string id) =>
        Units.FirstOrDefault(u => string.Equals(u.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? Units.FirstOrDefault(u => u.Aliases.Contains(id, StringComparer.OrdinalIgnoreCase));

    public static bool IsGlobalId(string id) => id.Length == 5 && id[0] == 'U' && id[1..].All(char.IsAsciiDigit);

    /// <summary>Follows merges to the unit that now owns the samples.</summary>
    public UnitRecord ResolveUnit(string id)
    {
        var unit = FindUnit(id) ?? throw new KeyNotFoundException($"知识库中没有单位 {id}。请先用 axis kb unit add 登记。");
        var guard = 0;
        while (unit.MergedInto is not null && guard++ < 20)
        {
            unit = FindUnit(unit.MergedInto) ?? unit;
        }

        return unit;
    }

    /// <summary>The next global id: one sequence for all kinds (the kind is a note, not part of the id).</summary>
    public string NextId()
    {
        var used = Units.Select(u => u.Id).Where(IsGlobalId).Select(id => int.Parse(id[1..])).DefaultIfEmpty(0).Max();
        return "U" + (used + 1).ToString("0000");
    }

    public UnitRecord AddUnit(UnitKind kind, string? id, string? name, string nameStatus, string? nameSource, string? notes)
    {
        if (kind == UnitKind.Empty)
        {
            throw new ArgumentException("EMPTY 是内置单位，不能再添加。");
        }

        id = string.IsNullOrWhiteSpace(id) ? NextId() : id.Trim();
        if (FindUnit(id) is not null)
        {
            throw new InvalidOperationException($"单位编号 {id} 已存在。");
        }

        var unit = new UnitRecord
        {
            Id = id,
            Kind = kind,
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            NameStatus = string.IsNullOrWhiteSpace(name) ? "none" : nameStatus,
            NameSource = nameSource,
            Notes = notes,
        };
        unit.History.Add($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm} 创建（{KindText(kind)}）");
        Units.Add(unit);
        return unit;
    }

    /// <summary>The user checked a 临时 unit: it becomes a normal unit and its samples count as confirmed.</summary>
    public void Confirm(UnitRecord unit)
    {
        if (!unit.Provisional)
        {
            return;
        }

        unit.Provisional = false;
        unit.History.Add($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm} 用户核对，取消临时标记");
        foreach (var sample in Samples.Where(s => s.UnitId == unit.Id))
        {
            sample.Status = "confirmed";
        }
    }

    public int Merge(string fromId, string intoId)
    {
        var from = FindUnit(fromId) ?? throw new KeyNotFoundException(fromId);
        var into = ResolveUnit(intoId);
        if (from.Id == into.Id)
        {
            throw new InvalidOperationException("不能把单位合并到自身。");
        }

        var moved = 0;
        foreach (var sample in Samples.Where(s => s.UnitId == from.Id))
        {
            var target = Path.Combine("samples", SafeName(into.Id), Path.GetFileName(sample.File));
            Directory.CreateDirectory(Path.GetDirectoryName(P(target))!);
            File.Move(P(sample.File), P(target), overwrite: false);
            sample.File = target;
            sample.UnitId = into.Id;
            moved++;
        }

        foreach (var shot in Screenshots)
        {
            foreach (var key in shot.Truth.Where(kv => kv.Value == from.Id).Select(kv => kv.Key).ToList())
            {
                shot.Truth[key] = into.Id;
            }
        }

        from.MergedInto = into.Id;
        from.History.Add($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm} 合并到 {into.Id}（移动样本 {moved} 个）");
        into.History.Add($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm} 并入 {from.Id}（样本 {moved} 个）");
        return moved;
    }

    /// <summary>
    /// Gives every unit (except EMPTY) a global id U0001… in creation order. The old id becomes an alias, sample files
    /// move to the new folder, and screenshot truths and merge links follow. Returns (old, new) pairs; the caller commits.
    /// </summary>
    public List<(string Old, string New)> MigrateToGlobalIds()
    {
        var renames = new List<(string Old, string New)>();
        var next = Units.Select(u => u.Id).Where(IsGlobalId).Select(id => int.Parse(id[1..])).DefaultIfEmpty(0).Max();
        foreach (var unit in Units.Where(u => u.Id != EmptyUnitId && !IsGlobalId(u.Id)).OrderBy(u => u.CreatedAt).ThenBy(u => u.Id, StringComparer.Ordinal).ToList())
        {
            var newId = "U" + (++next).ToString("0000");
            renames.Add((unit.Id, newId));
            unit.Aliases.Add(unit.Id);
            unit.History.Add($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm} 改用全局编号 {newId}（原编号 {unit.Id} 保留为别名）");
            unit.Id = newId;
        }

        var map = renames.ToDictionary(r => r.Old, r => r.New, StringComparer.OrdinalIgnoreCase);
        foreach (var unit in Units.Where(u => u.MergedInto is not null && map.ContainsKey(u.MergedInto)))
        {
            unit.MergedInto = map[unit.MergedInto!];
        }

        foreach (var sample in Samples.Where(s => map.ContainsKey(s.UnitId)))
        {
            sample.UnitId = map[sample.UnitId];
            var target = Path.Combine("samples", SafeName(sample.UnitId), Path.GetFileName(sample.File));
            if (!string.Equals(target, sample.File, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(P(target))!);
                File.Move(P(sample.File), P(target), overwrite: false);
                sample.File = target;
            }
        }

        foreach (var shot in Screenshots)
        {
            foreach (var key in shot.Truth.Keys.Where(k => map.ContainsKey(shot.Truth[k])).ToList())
            {
                shot.Truth[key] = map[shot.Truth[key]];
            }
        }

        // Folders of the old ids are empty now.
        foreach (var (old, _) in renames)
        {
            var folder = P(Path.Combine("samples", SafeName(old)));
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }

        return renames;
    }

    public static string SafeName(string id) =>
        string.Concat(id.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    // ---------- Screenshots ----------

    public ScreenshotRecord AddScreenshot(string imagePath, SourceInfo source, string? gameVersion, string split, string? notes, out bool alreadyPresent)
    {
        var bytes = File.ReadAllBytes(imagePath);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var existing = Screenshots.FirstOrDefault(s => s.Sha256 == sha);
        if (existing is not null)
        {
            alreadyPresent = true;
            return existing;
        }

        using var mat = ImageIO.Read(imagePath);
        var id = "S" + (Screenshots.Count + 1).ToString("00000");
        var extension = Path.GetExtension(imagePath).ToLowerInvariant();
        var keepJpeg = extension is ".jpg" or ".jpeg";
        var file = Path.Combine("screenshots", $"{id}_{sha[..8]}{(keepJpeg ? ".jpg" : ".png")}");
        Directory.CreateDirectory(P("screenshots"));
        if (keepJpeg || extension == ".png")
        {
            // Stored byte for byte: video frames stay JPEG, screenshots stay PNG.
            File.WriteAllBytes(P(file), bytes);
        }
        else
        {
            ImageIO.WritePng(P(file), mat);
        }

        var record = new ScreenshotRecord
        {
            Id = id,
            File = file,
            Sha256 = sha,
            Width = mat.Width,
            Height = mat.Height,
            Source = source,
            GameVersion = gameVersion,
            Split = split,
            AddedInKbVersion = Version + 1,
            Notes = notes,
        };
        Screenshots.Add(record);
        alreadyPresent = false;
        return record;
    }

    /// <summary>
    /// Re-saves video-frame screenshots stored as PNG as JPEG (quality <paramref name="quality"/>): the frames came from
    /// lossy video, so PNG only cost space. Samples are separate crops and are not touched. Returns (files, bytes before,
    /// bytes after); the caller commits.
    /// </summary>
    public (int Files, long Before, long After) ConvertVideoFramesToJpeg(int quality = 95)
    {
        var (files, before, after) = (0, 0L, 0L);
        foreach (var shot in Screenshots.Where(s => s.Source.Type == "user-video" && s.File.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
        {
            var oldPath = P(shot.File);
            before += new FileInfo(oldPath).Length;
            using var mat = ImageIO.Read(oldPath);
            Cv2.ImEncode(".jpg", mat, out var jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, quality));
            var newFile = Path.ChangeExtension(shot.File, ".jpg");
            File.WriteAllBytes(P(newFile), jpeg);
            File.Delete(oldPath);
            shot.File = newFile;
            shot.Sha256 = Convert.ToHexString(SHA256.HashData(jpeg)).ToLowerInvariant();
            shot.Notes = (shot.Notes is null ? string.Empty : shot.Notes + "；") + $"已由 PNG 转存为 JPEG（质量 {quality}）";
            after += jpeg.Length;
            files++;
        }

        return (files, before, after);
    }

    public ScreenshotRecord GetScreenshot(string id) =>
        Screenshots.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"知识库中没有截图 {id}。");

    public Mat ReadScreenshot(ScreenshotRecord record) => ImageIO.Read(P(record.File));

    // ---------- Samples ----------

    public SampleRecord AddSample(
        string unitId,
        ScreenshotRecord shot,
        string slot,
        RelRect rect,
        Mat crop,
        string labelSource,
        string status,
        string? notes,
        RelRect? feature = null,
        List<RelRect>? ignore = null)
    {
        var unit = ResolveUnit(unitId);
        var png = ImageIO.EncodePng(crop);
        var sha = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
        var id = "A" + (Samples.Count + 1).ToString("000000");
        var file = Path.Combine("samples", SafeName(unit.Id), $"{id}_{shot.Id}_{slot}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(P(file))!);
        File.WriteAllBytes(P(file), png);
        var sample = new SampleRecord
        {
            Id = id,
            UnitId = unit.Id,
            ScreenshotId = shot.Id,
            Slot = slot,
            Rect = rect,
            File = file,
            Sha256 = sha,
            LabelSource = labelSource,
            Status = status,
            AddedInKbVersion = Version + 1,
            Notes = notes,
            Feature = feature,
            Ignore = ignore,
        };
        Samples.Add(sample);
        return sample;
    }

    public Mat ReadSample(SampleRecord sample) => ImageIO.Read(P(sample.File));

    /// <summary>Moves a wrongly filed sample to its correct unit (file, label and the screenshot's truth). The caller commits.</summary>
    public SampleRecord RelabelSample(string sampleId, string unitId)
    {
        var sample = Samples.FirstOrDefault(s => s.Id.Equals(sampleId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"知识库中没有样本 {sampleId}。");
        var unit = ResolveUnit(unitId);
        var target = Path.Combine("samples", SafeName(unit.Id), Path.GetFileName(sample.File));
        Directory.CreateDirectory(Path.GetDirectoryName(P(target))!);
        File.Move(P(sample.File), P(target), overwrite: false);
        sample.File = target;
        sample.UnitId = unit.Id;
        if (Screenshots.FirstOrDefault(s => s.Id == sample.ScreenshotId) is { } shot)
        {
            shot.Truth[sample.Slot] = unit.Id;
        }

        return sample;
    }

    // ---------- Layouts ----------

    public LayoutProfile? LayoutFor(double aspect) =>
        Layouts.Where(l => aspect >= l.AspectMin && aspect <= l.AspectMax)
            .OrderBy(l => l.AspectMax - l.AspectMin)
            .FirstOrDefault();

    public void UpsertLayout(LayoutProfile layout)
    {
        Layouts.RemoveAll(l => l.Id == layout.Id);
        Layouts.Add(layout);
    }

    public void AppendCorrection(string line) =>
        File.AppendAllText(P("corrections.log"), $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}\t{line}{Environment.NewLine}", new UTF8Encoding(false));

    public long DiskBytes() =>
        Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
}
