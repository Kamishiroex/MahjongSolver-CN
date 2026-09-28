// SPDX-License-Identifier: AGPL-3.0-or-later
// UI-only theme. No game state, engine settings, or automatic actions are persisted here.
// Native blur is deliberately left to Dalamud WindowHost; do not add a second blur pass.
using System;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Mahjong.Plugin.CN.Ui;

internal static class GlassTheme
{
    private static readonly string[] AccentNames = ["青玉", "雾蓝", "暖金"];
    private static readonly Vector4[] Accents =
    [
        new(0.43f, 0.80f, 0.70f, 1f),
        new(0.48f, 0.68f, 0.94f, 1f),
        new(0.88f, 0.73f, 0.47f, 1f),
    ];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static Appearance options = new();
    private static string? path;
    private static string? storageError;
    private static bool initialized;
    private static bool futureSchema;
    private static readonly object saveGate = new();
    private static System.Threading.Tasks.Task saveTask = System.Threading.Tasks.Task.CompletedTask;

    // UI thread only. Lazy initialization reads the file once, not on every frame.
    internal static void Initialize(string configDirectory)
    {
        if (initialized) return;
        initialized = true;
        try
        {
            path = Path.Combine(configDirectory, "ui-glass.json");
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 65536)
                    throw new IOException("外观配置文件超过 64 KiB。");
                string json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                int schema = 0;
                if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                    (doc.RootElement.TryGetProperty("SchemaVersion", out var version) && (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out schema))))
                    throw new JsonException("外观配置格式不正确。");
                if (schema > 1) { futureSchema = true; throw new JsonException("外观配置版本较新，不会覆盖。"); }
                if (schema < 0) throw new JsonException("外观配置版本无效。");
                options = JsonSerializer.Deserialize<Appearance>(json, JsonOptions) ?? new();
                if (schema == 0 && !File.Exists(path + ".v0.bak")) File.Copy(path, path + ".v0.bak");
            }
            Normalize();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            options = new();
            storageError = "外观配置读取失败，已使用默认外观：" + ex.Message;
        }
    }

    internal static bool ModernLayout => options.ModernLayout;
    internal static bool HighlightDiscard => options.HighlightDiscard;
    internal static Vector4 Accent => options.Enabled ? Accents[options.Accent] : new(0.43f, 0.80f, 0.70f, 1f);
    internal static readonly Vector4 Warning = new(1f, 0.43f, 0.36f, 1f);
    internal static float Scale => Math.Clamp(ImGuiHelpers.GlobalScale, 0.5f, 4f);

    // PreDraw/PostDraw span ImGui.Begin/End. COLORS ONLY: WindowHost pushes/pops
    // its own Alpha variables across these hooks; plugin style variables must not
    // interleave with that stack. Content variables are scoped inside Draw instead.
    internal static StyleScope PushWindowColors()
    {
        var scope = new StyleScope();
        if (!options.Enabled) return scope;
        try
        {
            var accent = Accents[options.Accent];
            scope.Color(ImGuiCol.Text, new(0.92f, 0.95f, 0.97f, 1f));
            scope.Color(ImGuiCol.TextDisabled, new(0.60f, 0.67f, 0.73f, 1f));
            scope.Color(ImGuiCol.WindowBg, new(0.045f, 0.070f, 0.092f, options.Opacity));
            scope.Color(ImGuiCol.ChildBg, Vector4.Zero);
            scope.Color(ImGuiCol.PopupBg, new(0.06f, 0.085f, 0.11f, 0.98f));
            scope.Color(ImGuiCol.Border, new(0.75f, 0.86f, 0.91f, 0.17f));
            scope.Color(ImGuiCol.BorderShadow, Vector4.Zero);
            scope.Color(ImGuiCol.TitleBg, new(0.04f, 0.065f, 0.09f, options.Opacity));
            scope.Color(ImGuiCol.TitleBgActive, new(0.07f, 0.11f, 0.14f, options.Opacity));
            scope.Color(ImGuiCol.TitleBgCollapsed, new(0.04f, 0.065f, 0.09f, options.Opacity));
            scope.Color(ImGuiCol.FrameBg, new(0.15f, 0.20f, 0.24f, 0.72f));
            scope.Color(ImGuiCol.FrameBgHovered, Tint(accent, 0.23f));
            scope.Color(ImGuiCol.FrameBgActive, Tint(accent, 0.31f));
            scope.Color(ImGuiCol.Button, new(0.17f, 0.25f, 0.29f, 0.70f));
            scope.Color(ImGuiCol.ButtonHovered, Tint(accent, 0.30f));
            scope.Color(ImGuiCol.ButtonActive, Tint(accent, 0.40f));
            scope.Color(ImGuiCol.Header, Tint(accent, 0.19f));
            scope.Color(ImGuiCol.HeaderHovered, Tint(accent, 0.27f));
            scope.Color(ImGuiCol.HeaderActive, Tint(accent, 0.34f));
            scope.Color(ImGuiCol.CheckMark, accent);
            scope.Color(ImGuiCol.SliderGrab, Tint(accent, 0.80f));
            scope.Color(ImGuiCol.SliderGrabActive, accent);
            scope.Color(ImGuiCol.Separator, new(0.70f, 0.80f, 0.86f, 0.15f));
            scope.Color(ImGuiCol.Tab, new(0.09f, 0.14f, 0.18f, 0.75f));
            scope.Color(ImGuiCol.TabHovered, Tint(accent, 0.30f));
            scope.Color(ImGuiCol.ScrollbarBg, new(0.02f, 0.04f, 0.06f, 0.15f));
            scope.Color(ImGuiCol.ScrollbarGrab, new(0.55f, 0.65f, 0.70f, 0.35f));
            scope.Color(ImGuiCol.ScrollbarGrabHovered, Tint(accent, 0.55f));
            scope.Color(ImGuiCol.ScrollbarGrabActive, Tint(accent, 0.75f));
            scope.Color(ImGuiCol.TextSelectedBg, Tint(accent, 0.30f));
            return scope;
        }
        catch { scope.Dispose(); throw; }
    }

    internal static StyleScope PushContent()
    {
        var scope = new StyleScope();
        if (!options.Enabled) return scope;
        try
        {
            float scale = Scale;
            scope.Var(ImGuiStyleVar.ChildRounding, options.Radius * scale);
            scope.Var(ImGuiStyleVar.PopupRounding, 8f * scale);
            scope.Var(ImGuiStyleVar.FrameRounding, Math.Min(options.Radius, 6f) * scale);
            scope.Var(ImGuiStyleVar.GrabRounding, 6f * scale);
            scope.Var(ImGuiStyleVar.TabRounding, 6f * scale);
            scope.Var(ImGuiStyleVar.FrameBorderSize, 1f * scale);
            scope.Var(ImGuiStyleVar.WindowPadding, new Vector2(16, 14) * scale);
            scope.Var(ImGuiStyleVar.FramePadding, new Vector2(10, 6) * scale);
            scope.Var(ImGuiStyleVar.ItemSpacing, new Vector2(10, 8) * scale);
            // Do not push ImGuiStyleVar.Alpha: text must not fade with the backdrop.
            return scope;
        }
        catch { scope.Dispose(); throw; }
    }

    internal static void DrawSettings()
    {
        ImGui.TextUnformatted("青玉玻璃 · 界面外观");
        if (ImGui.Button("清透")) { options.Opacity = 0.70f; Save(); }
        ImGui.SameLine();
        if (ImGui.Button("标准")) { options.Opacity = 0.84f; Save(); }
        ImGui.SameLine();
        if (ImGui.Button("高可读")) { options.Opacity = 0.98f; Save(); }
        bool modern = options.ModernLayout;
        if (ImGui.Checkbox("使用侧栏与卡片布局", ref modern))
        { options.ModernLayout = modern; Save(); }
        ImGui.TextWrapped("取消勾选可切回经典标签页；切换外观不会启动、停止或恢复打牌。");
        bool enabled = options.Enabled;
        bool highlight = options.HighlightDiscard;
        if (ImGui.Checkbox("游戏手牌显示建议高亮", ref highlight)) { options.HighlightDiscard = highlight; Save(); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("只标记当前稳定、合法的弃牌建议；不代替玩家操作。暂停、动画或建议失效时隐藏。");
        if (ImGui.Checkbox("启用玻璃质感主题", ref enabled))
        { options.Enabled = enabled; Save(); }
        ImGui.BeginDisabled(!options.Enabled);
        try
        {
            float opacity = options.Opacity;
            if (ImGui.SliderFloat("背景不透明度", ref opacity, 0.60f, 1f, "%.2f")) options.Opacity = opacity;
            if (ImGui.IsItemDeactivatedAfterEdit()) Save();
            float radius = options.Radius;
            if (ImGui.SliderFloat("卡片与控件圆角", ref radius, 0f, 16f, "%.0f")) options.Radius = radius;
            if (ImGui.IsItemDeactivatedAfterEdit()) Save();
            int accent = options.Accent;
            if (ImGui.Combo("强调色", ref accent, AccentNames, AccentNames.Length))
            { options.Accent = accent; Save(); }
        }
        finally { ImGui.EndDisabled(); }
        ImGui.TextWrapped("真实背景模糊由 Dalamud 窗口菜单管理。点击标题栏三横线，在 Background Blur / 背景模糊中调整；窗口需位于游戏画面内。");
        ImGui.TextWrapped("建议把框架的整窗 Opacity 保持为 100%，只在这里调整背景。关闭本主题恢复原有框架配色，不改变打牌模式。");
        if (ImGui.Button("恢复默认外观")) { options = new(); Save(); }
        if (storageError is not null) ImGui.TextWrapped(storageError);
    }

    private static Vector4 Tint(Vector4 value, float alpha) => new(value.X, value.Y, value.Z, alpha);
    private static void Normalize()
    {
        options.Opacity = float.IsFinite(options.Opacity) ? Math.Clamp(options.Opacity, 0.60f, 1f) : 0.84f;
        options.Radius = float.IsFinite(options.Radius) ? Math.Clamp(options.Radius, 0f, 16f) : 12f;
        options.Accent = Math.Clamp(options.Accent, 0, Accents.Length - 1);
    }
    private static void Save()
    {
        Normalize();
        if (futureSchema) { storageError = "未来版本外观配置保持原样，请先备份并处理版本差异。"; return; }
        string json = JsonSerializer.Serialize(options, JsonOptions);
        lock (saveGate) saveTask = saveTask.ContinueWith(_ => SaveCore(json), System.Threading.Tasks.TaskScheduler.Default);
    }
    private static void SaveCore(string json)
    {
        if (path is null) { storageError = "外观配置尚未初始化，未保存。"; return; }
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && !File.Exists(path + ".before-save.bak")) File.Copy(path,path + ".before-save.bak");
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
            storageError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { storageError = "外观保存失败，本次会话仍使用新设置：" + ex.Message; }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal sealed class Appearance
    {
        public Appearance() { }
        public int SchemaVersion { get; set; } = 1;
        public bool Enabled { get; set; } = true;
        public bool ModernLayout { get; set; } = true;
        public bool HighlightDiscard { get; set; } = true;
        public float Opacity { get; set; } = 0.70f;
        public float Radius { get; set; } = 12f;
        public int Accent { get; set; }
    }
    internal sealed class StyleScope : IDisposable
    {
        private int colors;
        private int variables;
        internal void Color(ImGuiCol name, Vector4 value) { ImGui.PushStyleColor(name, value); colors++; }
        internal void Var(ImGuiStyleVar name, float value) { ImGui.PushStyleVar(name, value); variables++; }
        internal void Var(ImGuiStyleVar name, Vector2 value) { ImGui.PushStyleVar(name, value); variables++; }
        public void Dispose()
        {
            if (variables > 0) { ImGui.PopStyleVar(variables); variables = 0; }
            if (colors > 0) { ImGui.PopStyleColor(colors); colors = 0; }
        }
    }
}
