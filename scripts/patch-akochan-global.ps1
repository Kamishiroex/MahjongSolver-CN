[CmdletBinding()]
param([Parameter(Mandatory=$true)][string] $SourceDirectory,
      [Parameter(Mandatory=$true)][string] $CounterPatchedTypes,
      [Parameter(Mandatory=$true)][string] $OutputDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$utf8 = New-Object Text.UTF8Encoding($false, $true)
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
foreach ($candidate in @($SourceDirectory,$CounterPatchedTypes,$OutputDirectory)) {
    $resolved = [IO.Path]::GetFullPath($candidate)
    if (-not $resolved.StartsWith($root + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Global patch paths must be inside this checkout.' }
    $ancestor = $resolved
    while ($ancestor) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Global patch path contains reparse point.' }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
}
$head = & git -C $SourceDirectory rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $head -ne '53188a0b926fbab38177f88c3cd87d554cf412af') { throw 'AKOCHAN_GLOBAL_SOURCE_MISMATCH' }
if ((Get-FileHash -LiteralPath $CounterPatchedTypes -Algorithm SHA256).Hash.ToLowerInvariant() -ne 'dd77887b85aa7409a3141011e43012b2c1b85ac7d9b5fc70062e1c7089ac05f4') { throw 'AKOCHAN_GLOBAL_COUNTER_PATCH_MISMATCH' }
function Replace-One([string] $Text, [string] $Before, [string] $After) {
    if ($Text.Split([string[]]@($Before),[StringSplitOptions]::None).Length -ne 2) { throw "Global patch anchor mismatch: $Before" }
    return $Text.Replace($Before,$After)
}
$types = [IO.File]::ReadAllText($CounterPatchedTypes).Replace("`r`n","`n")
$include = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'native/akochan-global-state.inc')).Replace("`r`n","`n")
$types = Replace-One $types '#include "types.hpp"' ('#include "types.hpp"' + "`n" + $include)
$types = Replace-One $types 'Game_State get_game_state(const Moves& game_record) {' @'
Game_State get_game_state(const Moves& game_record) {
    mjcn_current_snapshot = mjcn_find_snapshot(game_record);
    if (mjcn_current_snapshot.is_object()) return mjcn_read_state(mjcn_current_snapshot);
'@
$types = Replace-One $types 'bool is_reach_accepted(const Moves& game_record, const int pid) {' @'
bool is_reach_accepted(const Moves& game_record, const int pid) {
    const auto snapshot = mjcn_find_snapshot(game_record);
    if (snapshot.is_object()) return snapshot["players"][pid]["riichi_established"].bool_value();
'@
$types = Replace-One $types 'bool is_ippatsu_valid(const Moves& game_record, const int pid) {' @'
bool is_ippatsu_valid(const Moves& game_record, const int pid) {
    const auto snapshot = mjcn_find_snapshot(game_record);
    if (snapshot.is_object()) return snapshot["players"][pid]["ippatsu"].bool_value();
'@
$types = Replace-One $types 'std::pair<int, int> count_tsumo_num(const Moves& game_record) {' @'
std::pair<int, int> count_tsumo_num(const Moves& game_record) {
    const auto snapshot = mjcn_find_snapshot(game_record);
    if (snapshot.is_object()) return std::pair<int,int>(70-snapshot["wall_remaining"].int_value(),0);
'@
$types = Replace-One $types 'std::fill(furiten_flags.begin(), furiten_flags.end(), false);' @'
std::fill(furiten_flags.begin(), furiten_flags.end(), false);
    const auto furiten_snapshot = mjcn_find_snapshot(game_record);
    if (furiten_snapshot.is_object()) {
        for (const auto& discard : game_state.player_state[pid].kawa)
            furiten_flags[haikind(discard.hai)] = true;
        // A positively observed missed winning window blocks every current wait,
        // not just the tile that was passed. Unknown/false never erases the river.
        if (pid == furiten_snapshot["our_player"].int_value() &&
            (furiten_snapshot["own_temporary_furiten"].bool_value() || furiten_snapshot["own_riichi_furiten"].bool_value())) {
            for (int kind = 1; kind < 38; kind++) if (kind % 10 != 0) furiten_flags[kind] = true;
            return furiten_flags;
        }
        // A partial public record has no trustworthy reach_accepted boundary or
        // complete pass/draw history. Upstream's reverse scan would treat ALL
        // earlier opponent discards as passed after the CURRENT accepted riichi.
        // Preserve proven own-river furiten; do not manufacture temporary or
        // post-riichi pass furiten from missing chronology. Game legal actions
        // still gate our current Ron. Complete native replays retain upstream logic.
        if (!furiten_snapshot["history_complete"].bool_value()) return furiten_flags;
    }
'@
$mjutil = [IO.File]::ReadAllText((Join-Path $SourceDirectory 'ai_src/mjutil.cpp')).Replace("`r`n","`n")
$mjutil = Replace-One $mjutil 'Hai_Array get_hai_visible_all(const Game_State& game_state) {' @'
extern Hai_Array mjcn_missing_claimed_tiles();
Hai_Array get_hai_visible_all(const Game_State& game_state) {
'@
$mjutil = Replace-One $mjutil 'hai_visible_all[hai] = 0;' 'hai_visible_all[hai] = mjcn_missing_claimed_tiles()[hai];'
$mjutil += @'

// Expose the exact public tile inventory used by the native selector for audit.
__declspec(dllexport) Hai_Array mjcn_effective_visible(const Moves& record) {
    return get_hai_visible_all(get_game_state(record));
}
'@
$main = [IO.File]::ReadAllText((Join-Path $SourceDirectory 'main.cpp')).Replace("`r`n","`n")
$main = Replace-One $main 'int main(int argc,char* argv[]) {' 'int mjcn_upstream_main(int argc,char* argv[]) {'
$winContext = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'native/akochan-win-context.inc')).Replace("`r`n","`n")
$selector = [IO.File]::ReadAllText((Join-Path $SourceDirectory 'ai_src/selector.cpp')).Replace("`r`n","`n")
$selector = Replace-One $selector '#include "selector.hpp"' ('#include "selector.hpp"' + "`n" + $winContext)
$selector = Replace-One $selector 'const int haitei_han = (count_tsumo_num_all(game_record) == 70 ? 1 : 0);' 'const int haitei_han = mjcn_tsumo_incident_han(game_record, my_pid, count_tsumo_num_all(game_record) == 70 ? 1 : 0);'
$selector = Replace-One $selector 'const int incident_han = (count_tsumo_num_all(game_record) == 70 ? 1 : 0) + (current_action["type"] == "kakan" ? 1 : 0);' 'const int incident_han = mjcn_ron_incident_han(game_record, my_pid, (count_tsumo_num_all(game_record) == 70 ? 1 : 0) + (current_action["type"] == "kakan" ? 1 : 0));'
$bridge = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'native/akochan-global-main.cpp')).Replace("`r`n","`n")
$bridge = Replace-One $bridge '#include "ai_src/selector.hpp"' ('#include "ai_src/selector.hpp"' + "`n" + $winContext)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$typesPath = Join-Path $OutputDirectory 'types.cpp'
$mjutilPath = Join-Path $OutputDirectory 'mjutil.cpp'
$mainPath = Join-Path $OutputDirectory 'upstream-main.cpp'
$selectorPath = Join-Path $OutputDirectory 'selector.cpp'
$bridgePath = Join-Path $OutputDirectory 'global-main.cpp'
foreach ($entry in @(@($typesPath,$types),@($mjutilPath,$mjutil),@($mainPath,$main),@($selectorPath,$selector),@($bridgePath,$bridge))) {
    if (Test-Path -LiteralPath $entry[0]) { throw 'Global patch refuses to overwrite existing output.' }
    [IO.File]::WriteAllText($entry[0],$entry[1].Replace("`r`n","`n"),$utf8)
}
[pscustomobject]@{ id='mjcn-public-snapshot-v5'; types=$typesPath; mjutil=$mjutilPath; main=$mainPath;
    bridge=$bridgePath; selector=$selectorPath;
    selectorSha256=(Get-FileHash -LiteralPath $selectorPath -Algorithm SHA256).Hash.ToLowerInvariant();
    typesSha256=(Get-FileHash -LiteralPath $typesPath -Algorithm SHA256).Hash.ToLowerInvariant();
    mjutilSha256=(Get-FileHash -LiteralPath $mjutilPath -Algorithm SHA256).Hash.ToLowerInvariant();
    bridgeSha256=(Get-FileHash -LiteralPath $bridgePath -Algorithm SHA256).Hash.ToLowerInvariant();
    patchScriptSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant() }
