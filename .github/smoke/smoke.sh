#!/usr/bin/env bash
# Drives the installed Fuse tool end to end on a generated multi-project repository, the way the smoke
# workflow (.github/workflows/smoke.yml) packs and installs it: fuse init, the post-edit and stop hooks
# answered from payloads on standard input, fuse check (every change, named files and a scoped check),
# fuse session, FUSE_SESSION, 10 and 20 concurrent checks with fuse status probes, fuse stop, fuse test
# and fuse build.
#
# Usage: .github/smoke/smoke.sh <feed> <version>
# <feed> holds the packages the workflow packed (the Fuse pointer package and this platform's package).
# Runs in bash on Linux, macOS and Windows (Git Bash). Needs the .NET SDK and git.
set -euo pipefail

feed=$(cd "${1:?feed}" && pwd)
version=${2:?version}
work=$(mktemp -d)
tools="$work/tools"

fail() { echo "smoke: $*" >&2; exit 1; }

# Runs a command, captures what it prints on either stream into $output and its exit code into $code,
# and prints the output so the workflow log holds it either way.
capture() {
    set +e
    output=$("$@" 2>&1)
    code=$?
    set -e
    echo "$output"
}

assert_code() { # <expected> <what>
    [ "$code" = "$1" ] || fail "$2 exited $code, expected $1: $output"
}

assert_contains() { # <text> <what>
    case "$output" in *"$1"*) ;; *) fail "$2 did not print '$1': $output" ;; esac
}

assert_not_contains() { # <text> <what>
    case "$output" in *"$1"*) fail "$2 printed '$1', which it must leave out: $output" ;; *) ;; esac
}

# A path as a hook payload names it: JSON with forward slashes on every platform.
json_path() {
    if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else echo "$1"; fi
}

# Runs fuse hook claude <event> with a payload on standard input, as a harness runs it.
hook() { # <event> <payload>
    set +e
    output=$(printf '%s' "$2" | "$fuse" hook claude "$1" 2>&1)
    code=$?
    set -e
    echo "$output"
}

dotnet tool install --tool-path "$tools" Fuse --version "$version" --add-source "$feed"
fuse="$tools/fuse"
[ -e "$fuse.exe" ] && fuse="$fuse.exe"
[ -e "$fuse" ] || fail "dotnet tool install created no fuse command in $tools"

capture "$fuse" --version
assert_code 0 "fuse --version"
[ "$output" = "$version" ] || fail "fuse --version printed '$output', expected '$version'"
echo "smoke: installed fuse $version from $feed"

# A multi-project repository: a library, a console project that uses it and a test project that tests it.
repo="$work/repo"
mkdir -p "$repo" && cd "$repo"
git init -q
git config user.email "ci@fuse.invalid"
git config user.name "Fuse CI"
dotnet new sln -n Smoke >/dev/null
dotnet new classlib -n Lib -o Lib >/dev/null
dotnet new console -n App -o App >/dev/null
dotnet new xunit -n Tests -o Tests >/dev/null
sln_file=$(echo Smoke.sln*)
dotnet sln "$sln_file" add Lib/Lib.csproj App/App.csproj Tests/Tests.csproj >/dev/null
dotnet add App/App.csproj reference Lib/Lib.csproj >/dev/null
dotnet add Tests/Tests.csproj reference Lib/Lib.csproj >/dev/null
rm -f Lib/Class1.cs Tests/UnitTest1.cs

write_calc() { printf '%s\n' 'namespace Lib;' '' 'public static class Calc' '{' "    public static int Add(int a, int b) => $1;" '}' > Lib/Calc.cs; }
write_app() { printf '%s\n' 'using Lib;' '' 'Console.WriteLine(Calc.Add(1, 2));' "$1" > App/Program.cs; }

write_calc 'a + b'
write_app ''
cat > Tests/CalcTests.cs <<'EOF'
using Lib;

public class CalcTests
{
    [Fact]
    public void Add_adds_two_numbers() => Assert.Equal(3, Calc.Add(1, 2));
}
EOF
printf 'bin/\nobj/\n' > .gitignore
git add -A && git commit -qm init
dotnet restore "$sln_file" >/dev/null
echo "smoke: generated Lib, App and Tests in $repo"

calc_path=$(json_path "$repo/Lib/Calc.cs")
app_path=$(json_path "$repo/App/Program.cs")
repo_path=$(json_path "$repo")
post_edit() { # <session> <file>
    hook post-edit "{\"tool_name\":\"Edit\",\"tool_input\":{\"file_path\":\"$2\"},\"cwd\":\"$repo_path\",\"session_id\":\"$1\"}"
}
stop_hook() { # <session>
    hook stop "{\"cwd\":\"$repo_path\",\"session_id\":\"$1\"}"
}

echo "smoke: fuse init"
capture "$fuse" init
assert_code 0 "fuse init"
assert_contains "hooks registered" "fuse init"
[ -f .claude/settings.json ] || fail "fuse init wrote no .claude/settings.json"
grep -q 'fuse hook claude post-edit' .claude/settings.json || fail ".claude/settings.json holds no post-edit hook"
grep -q 'fuse hook claude stop' .claude/settings.json || fail ".claude/settings.json holds no stop hook"
git add -A && git commit -qm 'fuse init'

echo "smoke: clean check"
capture "$fuse" check
assert_code 0 "fuse check on a clean tree"
assert_contains "no errors introduced" "fuse check on a clean tree"

echo "smoke: post-edit hook with an introduced error, then fixed"
write_calc 'a + smokeMissingLib'
post_edit smoke-a "$calc_path"
assert_code 2 "the post-edit hook"
assert_contains "CS0103" "the post-edit hook"
assert_contains "smokeMissingLib" "the post-edit hook"
write_calc 'a + b'
post_edit smoke-a "$calc_path"
assert_code 0 "the post-edit hook after the fix"
assert_not_contains "CS0103" "the post-edit hook after the fix"

echo "smoke: fuse check, a named-file check and a scoped check"
write_calc 'a + smokeMissingLib'
capture "$fuse" check
assert_code 1 "fuse check"
assert_contains "smokeMissingLib" "fuse check"
capture "$fuse" check Lib/Calc.cs
assert_code 1 "fuse check Lib/Calc.cs"
assert_contains "smokeMissingLib" "fuse check Lib/Calc.cs"
capture "$fuse" check Lib
assert_code 1 "fuse check Lib"
assert_contains "smokeMissingLib" "fuse check Lib"
assert_contains "what it reaches" "fuse check Lib"
write_calc 'a + b'
capture "$fuse" check
assert_code 0 "fuse check after the fix"

echo "smoke: sessions and the stop hook"
write_calc 'a + smokeMissingLib'
post_edit smoke-a "$calc_path"
assert_code 2 "the post-edit hook that credits Lib/Calc.cs to smoke-a"
write_app 'Console.WriteLine(smokeMissingApp);'
capture "$fuse" session begin smoke-ext --paths 'App/**'
assert_code 0 "fuse session begin smoke-ext"
assert_contains "registered" "fuse session begin smoke-ext"
stop_hook smoke-a
assert_code 0 "the stop hook"
assert_contains '"decision":"block"' "the stop hook"
assert_contains "smokeMissingLib" "the stop hook"
assert_not_contains "smokeMissingApp" "the stop hook while App is registered to smoke-ext"
capture "$fuse" session end smoke-ext
assert_code 0 "fuse session end smoke-ext"
stop_hook smoke-a
assert_contains '"decision":"block"' "the stop hook after the registration ended"
assert_contains "smokeMissingApp" "the stop hook after the registration ended"
capture "$fuse" session end smoke-ext
assert_code 2 "fuse session end smoke-ext a second time"
assert_contains "no registered session named smoke-ext" "fuse session end smoke-ext a second time"

echo "smoke: FUSE_SESSION"
post_edit smoke-b "$app_path"
assert_code 2 "the post-edit hook that credits App/Program.cs to smoke-b"
assert_contains "smokeMissingApp" "the post-edit hook that credits App/Program.cs to smoke-b"
set +e
output=$(FUSE_SESSION='smoke-env' "$fuse" check App/Program.cs 2>&1)
code=$?
set -e
echo "$output"
assert_code 1 "FUSE_SESSION=smoke-env fuse check App/Program.cs"
assert_contains "smokeMissingApp" "FUSE_SESSION=smoke-env fuse check App/Program.cs"
set +e
output=$(FUSE_SESSION='smoke-env' "$fuse" check 2>&1)
code=$?
set -e
echo "$output"
assert_code 1 "FUSE_SESSION=smoke-env fuse check"
assert_contains "smokeMissingApp" "FUSE_SESSION=smoke-env fuse check"
assert_not_contains "smokeMissingLib" "FUSE_SESSION=smoke-env fuse check"
write_calc 'a + b'
write_app ''
capture "$fuse" check
assert_code 0 "fuse check after both fixes"
stop_hook smoke-a
assert_not_contains '"decision":"block"' "the stop hook on a clean tree"

# Runs <count> fuse check calls at once against the broken Lib/Calc.cs, with three fuse status probes
# beside them. With <distinct-sessions> each check names its own FUSE_SESSION.
concurrent_checks() { # <count> <distinct-sessions: 0 or 1>
    local count=$1 distinct=$2 dir="$work/concurrent-$1-$2" i pid
    mkdir -p "$dir"
    local pids=()
    for i in $(seq 1 "$count"); do
        if [ "$distinct" = 1 ]; then
            ( code=0; FUSE_SESSION="smoke-par-$i" "$fuse" check >"$dir/check-$i.out" 2>&1 || code=$?; echo "$code" >"$dir/check-$i.code" ) &
        else
            ( code=0; "$fuse" check >"$dir/check-$i.out" 2>&1 || code=$?; echo "$code" >"$dir/check-$i.code" ) &
        fi
        pids+=($!)
    done
    for i in 1 2 3; do
        ( code=0; "$fuse" status >"$dir/status-$i.out" 2>&1 || code=$?; echo "$code" >"$dir/status-$i.code" ) &
        pids+=($!)
    done
    for pid in "${pids[@]}"; do wait "$pid" || true; done
    for i in $(seq 1 "$count"); do
        code=$(cat "$dir/check-$i.code")
        if [ "$distinct" = 1 ]; then
            # A caller with a session of its own is told only its own errors, so it answers 0 or 1, never a failure.
            { [ "$code" = 0 ] || [ "$code" = 1 ]; } || fail "concurrent check $i of $count exited $code, expected 0 or 1: $(cat "$dir/check-$i.out")"
            grep -q '^fuse: ' "$dir/check-$i.out" || fail "concurrent check $i of $count printed no summary: $(cat "$dir/check-$i.out")"
        else
            [ "$code" = 1 ] || fail "concurrent check $i of $count exited $code, expected 1: $(cat "$dir/check-$i.out")"
            grep -q 'smokeMissingLib' "$dir/check-$i.out" || fail "concurrent check $i of $count printed no smokeMissingLib: $(cat "$dir/check-$i.out")"
        fi
    done
    for i in 1 2 3; do
        [ "$(cat "$dir/status-$i.code")" = 0 ] || fail "fuse status probe $i beside $count checks exited $(cat "$dir/status-$i.code"): $(cat "$dir/status-$i.out")"
        grep -q 'changed file' "$dir/status-$i.out" || fail "fuse status probe $i beside $count checks printed no tree line: $(cat "$dir/status-$i.out")"
    done
    echo "smoke: $count concurrent checks answered, with status probes beside them"
}

echo "smoke: 10 and 20 concurrent checks"
write_calc 'a + smokeMissingLib'
concurrent_checks 10 0
concurrent_checks 20 1
write_calc 'a + b'
capture "$fuse" check
assert_code 0 "fuse check after the concurrency rounds"

echo "smoke: fuse graph and fuse impact"
capture "$fuse" graph --json
assert_code 0 "fuse graph --json"
assert_contains "{" "fuse graph --json"
assert_contains "Lib" "fuse graph --json"
capture "$fuse" impact Lib/Calc.cs --json
assert_code 0 "fuse impact Lib/Calc.cs --json"
assert_contains "{" "fuse impact Lib/Calc.cs --json"

echo "smoke: fuse check --brief repair packet"
write_app 'Console.WriteLine(Calc.Ad(1, 2));'
capture "$fuse" check --brief
assert_code 1 "fuse check --brief on CS0117"
assert_contains "CS0117" "fuse check --brief on CS0117"
assert_contains "int Calc.Add(int a, int b)" "fuse check --brief on CS0117"
write_app ''
capture "$fuse" check
assert_code 0 "fuse check after restoring App/Program.cs"

echo "smoke: fuse gate --brief --test"
write_calc 'a + smokeMissingLib'
capture "$fuse" gate --brief --test
assert_code 1 "fuse gate --brief --test on broken code"
assert_contains "smokeMissingLib" "fuse gate --brief --test on broken code"
write_calc 'b + a'
capture "$fuse" gate --brief --test
assert_code 0 "fuse gate --brief --test after fix"
assert_contains "passed" "fuse gate --brief --test after fix"
write_calc 'a + b'
capture "$fuse" check
assert_code 0 "fuse check after fuse gate"

echo "smoke: fuse slim"
capture "$fuse" slim
assert_code 0 "fuse slim"
capture "$fuse" check
assert_code 0 "fuse check after fuse slim"
assert_contains "no errors introduced" "fuse check after fuse slim"

echo "smoke: fuse batch"
set +e
output=$(printf '{"request":"Ping"}\n{"request":"Status"}\n{"request":"CheckChanges","waitForLoad":true}\n' | "$fuse" batch 2>&1)
code=$?
set -e
echo "$output"
assert_code 0 "fuse batch"
assert_contains "Acknowledged" "fuse batch"
assert_contains "StatusAnswered" "fuse batch"
assert_contains "CheckAnswered" "fuse batch"

echo "smoke: fuse watch --all --json and fuse session guard"
watch_log="$work/watch-all.ndjson"
"$fuse" watch --all --json >"$watch_log" 2>&1 &
watch_pid=$!
sleep 1
capture "$fuse" session guard smoke-guard --freeze 'Lib/**'
assert_code 0 "fuse session guard --freeze"
printf '%s\n' 'namespace Lib;' '' 'public static class Calc' '{' '    public static int Add(int a, int b) => a + b;' '    public static int Sub(int a, int b) => a - b;' '}' > Lib/Calc.cs
capture "$fuse" check
{ [ "$code" = 0 ] || [ "$code" = 1 ]; } || fail "fuse check after frozen surface edit exited $code, expected 0 or 1: $output"
sleep 1
capture "$fuse" session guard smoke-guard --unfreeze
assert_code 0 "fuse session guard --unfreeze"
kill "$watch_pid" 2>/dev/null || true
wait "$watch_pid" 2>/dev/null || true
[ -s "$watch_log" ] || fail "fuse watch --all --json wrote no events"
grep -q '{' "$watch_log" || fail "fuse watch --all --json wrote no JSON event: $(cat "$watch_log")"
write_calc 'a + b'
capture "$fuse" check
assert_code 0 "fuse check after unfreeze"

echo "smoke: fuse judge --combine and --race"
patch_dir="$work/patches"
mkdir -p "$patch_dir"
write_calc 'b + a'
git diff --no-color -- Lib/Calc.cs > "$patch_dir/p1.patch"
write_calc 'a + b'
write_app 'Console.WriteLine(Calc.Add(2, 3));'
git diff --no-color -- App/Program.cs > "$patch_dir/p2.patch"
write_app 'Console.WriteLine(Calc.Ad(2, 3));'
git diff --no-color -- App/Program.cs > "$patch_dir/bad.patch"
write_app ''
capture "$fuse" judge --combine "$patch_dir/p1.patch" "$patch_dir/p2.patch" --json
assert_code 0 "fuse judge --combine"
assert_contains "{" "fuse judge --combine"
capture "$fuse" judge --race "$patch_dir/p2.patch" "$patch_dir/bad.patch" --json
assert_code 0 "fuse judge --race"
assert_contains "{" "fuse judge --race"
assert_contains "p2.patch" "fuse judge --race"

echo "smoke: shared hypervisor across secondary git worktree (zero-restore)"
capture "$fuse" status
assert_code 0 "fuse status before secondary worktree"
primary_pid=$(printf '%s' "$output" | sed -n 's/.*(pid \([0-9][0-9]*\)).*/\1/p' | head -n 1)
[ -n "$primary_pid" ] || fail "could not read engine pid from fuse status: $output"
wt2="$work/wt2"
git worktree add -q -b smoke-wt2 "$wt2" HEAD
(
    cd "$wt2"
    capture "$fuse" check
    assert_code 0 "zero-restore fuse check in secondary worktree"
    assert_contains "no errors introduced" "zero-restore fuse check in secondary worktree"
    capture "$fuse" status
    assert_code 0 "fuse status in secondary worktree"
    assert_contains "pid $primary_pid" "fuse status in secondary worktree shares primary engine PID"
    printf '%s\n' 'namespace Lib;' '' 'public static class Calc' '{' '    public static int Add(int a, int b) => a + smokeWtMissing;' '}' > Lib/Calc.cs
    capture "$fuse" check
    assert_code 1 "fuse check in secondary worktree with error"
    assert_contains "smokeWtMissing" "fuse check in secondary worktree with error"
    capture "$fuse" stop
    assert_code 0 "fuse stop in secondary worktree"
    assert_contains "released worktree" "fuse stop in secondary worktree"
)
capture "$fuse" check
assert_code 0 "primary worktree remains clean during secondary worktree error"
assert_not_contains "smokeWtMissing" "primary worktree check"
capture "$fuse" status
assert_code 0 "fuse status in primary after secondary release"
assert_contains "pid $primary_pid" "fuse status in primary after secondary release"
git worktree remove --force "$wt2"
git branch -D smoke-wt2 >/dev/null

echo "smoke: fuse stop"
capture "$fuse" status
assert_code 0 "fuse status"
assert_contains "engine running" "fuse status"
capture "$fuse" stop
assert_code 0 "fuse stop"
assert_contains "stopped the engine" "fuse stop"
capture "$fuse" status
assert_code 0 "fuse status after fuse stop"
assert_contains "no engine runs" "fuse status after fuse stop"

echo "smoke: fuse test"
write_calc 'b + a'
capture "$fuse" test
assert_code 0 "fuse test"
assert_contains "passed" "fuse test"

echo "smoke: fuse build"
capture "$fuse" build
assert_code 0 "fuse build"
assert_contains "build succeeded" "fuse build"
capture "$fuse" check
assert_code 0 "fuse check after fuse build"
assert_contains "no errors introduced" "fuse check after fuse build"

echo "smoke: the installed fuse $version passed"
