"""Architecture guard for the reviewed PR migration; behavioral tests remain mandatory."""
from pathlib import Path
import re

root = Path(__file__).resolve().parents[1]
shell = (root / 'Mahjong.Plugin.CN/MainWindow.Glass.cs').read_text(encoding='utf-8-sig')
classic = (root / 'Mahjong.Plugin.CN/MainWindow.cs').read_text(encoding='utf-8-sig')
theme = (root / 'Mahjong.Plugin.CN/Ui/GlassTheme.cs').read_text(encoding='utf-8-sig')
card = shell.split('private static void Card(', 1)[1]
assert 'BeginChild' not in card and 'AlwaysAutoResize' not in card
assert card.count('draw();') == 1, 'Card must execute actions only once.'
assert 'ChannelsSetCurrent(0)' in card and 'ChannelsMerge()' in card
assert 'finally { ImGui.EndTable(); }' in card
assert 'finally { ImGui.EndChild(); }' in shell
# Expiry notices may inspect qualification; the root Draw path must never gate pages.
root_draw = shell.split('public override void Draw()', 1)[1].split('private void DrawPersistentControls()', 1)[0]
assert 'DrawLegacy();' in shell and '!plugin.TestAccessUnlocked' not in root_draw
for view in ('DrawTableAutomation', 'DrawSettings', 'DrawJournal', 'DrawPlay', 'DrawDiagnostics', 'DrawAbout'):
    assert re.search(r'\bvoid '+view+r'\(', classic), view
for page in ('总览', '任务', '战绩与记录', '设置', '诊断'):
    assert f'"{page}"' in shell
color_scope = theme.split('internal static StyleScope PushWindowColors()', 1)[1].split('internal static StyleScope PushContent()', 1)[0]
assert 'scope.Var(' not in color_scope
assert 'scope.Var(ImGuiStyleVar.Alpha' not in theme
assert 'Initialize(' not in shell, 'Configuration loading is outside drawing.'
for forbidden in ('ReadProcessMemory', 'FireCallback', 'Process.Start', 'HttpClient', 'File.Read', 'File.Write'):
    assert forbidden not in shell + classic, forbidden
assert 'plugin.PausePlay();' in shell and 'plugin.Stop(' in shell
print('UI architecture checks passed: natural cards, five pages, legacy reachability, capability separation/pause and style scope.')
print('Source checks are not live rendering or game verification.')
