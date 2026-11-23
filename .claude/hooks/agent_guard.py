#!/usr/bin/env python3
"""Technical guard for Nexus PC Claude Code subagents.

Runs as a PreToolUse hook (Edit/Write/MultiEdit/NotebookEdit/Bash) for the main
session and every subagent, and as a SubagentStop hook with --release.

Policy (mirrors .claude/AGENT_RULES.md):
- read-only agents (code-architect, security-auditor, qa-engineer) cannot change
  files in the project, only build output (bin/, obj/, ...);
- each writing agent may change only its own zone;
- subagents never run git commands that change history or the working tree,
  never publish, never edit .claude/, CLAUDE.md, FEATURES.md, secrets or
  Bundle ID / signing lines;
- only one writing agent at a time may change project files (lock);
- the main session is asked (not blocked) before irreversible actions.

The hook is defence in depth: shell parsing is heuristic. Decisions are logged to
<tempdir>/nexus-agent-guard.log. Exit code is always 0; the decision is returned as
JSON so that "ask" is possible for the main session.
"""
from __future__ import annotations

import hashlib
import json
import os
import re
import shlex
import sys
import tempfile
import time

READ_ONLY = {"code-architect", "security-auditor", "qa-engineer"}

# Paths are relative to the project root; a trailing "/" means a directory.
ZONES: dict[str, list[str]] = {
    "ios-developer": ["ios/", "docs/IOS.md"],
    "windows-developer": ["windows/", "shared/", "docs/SETUP.md"],
    "android-developer": ["docs/ANDROID_ROADMAP.md", "android/"],
    "ui-ux-designer": ["docs/design/"],
    "backend-developer": ["backend/", "docs/BACKEND.md"],
    "web-developer": ["site/", "docs/SITE.md"],
    "release-manager": [
        ".github/workflows/", "tools/", "CHANGELOG.md", "README.md",
        "docs/SUMMARY.md", "docs/USER_GUIDE.md", "docs/APP_STORE_READINESS.md",
        "docs/PRIVACY_POLICY_DRAFT.md", "docs/APP_REVIEW_NOTES.md",
        "docs/RELEASE_CHECKLIST.md", "docs/RELEASE_READINESS.md", "docs/releases/",
    ],
}
NEXUS_AGENTS = READ_ONLY | set(ZONES)

# Build output: allowed for every agent (tests and builds write here; all are gitignored).
BUILD_DIRS = {"bin", "obj", "TestResults", ".build", "build", "DerivedData", "Payload"}

# Never changed by a subagent.
PROTECTED = [".claude/", ".git/", "CLAUDE.md", "FEATURES.md"]

SECRET_FILE = re.compile(
    r"(^|/)(\.env(\..*)?|.*\.(p12|pfx|pem|key|mobileprovision|keystore|jks|snk)|"
    r"credentials[^/]*\.json|.*secret[^/]*|settings\.local\.json)$",
    re.IGNORECASE,
)
# Lines that carry the Bundle ID or signing settings.
SIGNING_LINE = re.compile(
    r"PRODUCT_BUNDLE_IDENTIFIER|CFBundleIdentifier|CODE_SIGN|DEVELOPMENT_TEAM|"
    r"PROVISIONING_PROFILE|application-identifier|com\.apple\.developer|aps-environment"
)
VERSION_LINE = re.compile(r"MARKETING_VERSION|CURRENT_PROJECT_VERSION")

LOCK_TTL = 15 * 60

# --- Bash rules ---------------------------------------------------------------
# Irreversible or outward-facing: subagents are denied, the main session is asked.
IRREVERSIBLE = [
    (r"\bgit\s+push\b[^\n;|&]*(\s--force\b|\s-f\b|\s--force-with-lease\b|\s--delete\b|\s-d\b|\s--mirror\b|\s--tags\b|\s\+|\s:)", "force/delete/tag push"),
    (r"\bgit\s+push\b[^\n;|&]*\s(main|master)\b", "push to the default branch"),
    (r"\bgit\s+reset\s+[^\n;|&]*--hard\b", "git reset --hard"),
    (r"\bgit\s+clean\s+[^\n;|&]*-[a-zA-Z]*f", "git clean -f"),
    (r"\bgit\s+branch\s+[^\n;|&]*(-D\b|-d\b|--delete\b)", "branch deletion"),
    (r"\bgit\s+tag\s+(-[adfs]\b|[^-\s])", "tag creation/deletion"),
    (r"\bgh\s+(release|workflow\s+run|secret|repo\s+(delete|edit|rename))\b", "GitHub release/workflow/secret/repo change"),
    (r"\b(xcrun\s+(altool|notarytool)|fastlane|iTMSTransporter|Transporter)\b", "App Store upload"),
    (r"-exportArchive\b", "App Store export"),
    (r"\bdotnet\s+nuget\s+push\b", "package publish"),
    (r"NexusPC-Setup(\.exe)?\b[^\n;|&]*--purge\b", "installer --purge"),
    (r"\brm\s+-[a-zA-Z]*[rf][a-zA-Z]*\s+(/|~|\$HOME|/home|/root)(\s|$)", "recursive delete of a system/home directory"),
]
# Git commands that change history, the index, branches or the working tree.
GIT_WRITE = re.compile(
    r"\bgit\s+(-C\s+\S+\s+)?(commit|push|merge|rebase|cherry-pick|revert|am|apply|stash|checkout|switch|"
    r"restore|rm|mv|add|reset|clean|worktree|submodule|config|notes|replace|update-ref|filter-branch|gc|prune)\b"
)
GIT_BRANCH_WRITE = re.compile(r"\bgit\s+branch\s+[^\n;|&]*(-[dDmMcCf]\b|--(delete|move|copy|force|set-upstream))")
NETWORK_WRITE = re.compile(
    r"\b(curl|wget|http)\b[^\n;|&]*(\s-X\s*(POST|PUT|PATCH|DELETE)\b|\s--request\s+(POST|PUT|PATCH|DELETE)\b|"
    r"\s(-d|--data[a-z-]*|-F|--form|-T|--upload-file|--post-data|--post-file)\b)",
    re.IGNORECASE,
)
DEPENDENCY_ADD = re.compile(
    r"\bdotnet\s+(add\s+[^\n;|&]*package|new|sln|format)\b|\bswift\s+package\s+(add|update|resolve|init|edit)\b|"
    r"\bxcodegen\b|\bnpm\s+(i|install|add)\b(?![^\n;|&]*\s-g\b)|\byarn\s+add\b"
)
CMD_START = r"(?:^|[;&|(\n]\s*|\b(?:sudo|xargs|env|timeout\s+\S+)\s+)"
INPLACE = re.compile(CMD_START + r"(?:(?:sed|perl)\s+[^\n;|&]*\s-[a-zA-Z]*i|(?:patch|truncate|dd)\b)")
INLINE_CODE = re.compile(r"\b(python3?|node|ruby|perl)\b[^\n|;&]*(\s-c\s|\s-e\s|\s-\s|\s-$|<<)")
WRITE_TOKENS = re.compile(
    r"open\([^)]*['\"][wax+]|write_text|write_bytes|os\.remove|os\.unlink|\.unlink\(|shutil\.|os\.rename|"
    r"os\.replace|os\.makedirs|os\.mkdir|subprocess|os\.system|fs\.write|fs\.unlink|File\.write"
)
FILE_COMMANDS_ALL_ARGS = {"rm", "rmdir", "touch", "mkdir", "tee", "chmod", "chown", "mv", "unlink", "shred"}
FILE_COMMANDS_LAST_ARG = {"cp", "install", "ln", "rsync"}
WRAPPERS = {"sudo", "env", "nohup", "time", "command", "exec", "xargs", "timeout", "nice"}
OPERATORS = {";", "&&", "||", "|", "&", "(", ")", "\n", "|&"}


def project_dir(data: dict) -> str:
    return os.path.realpath(os.environ.get("CLAUDE_PROJECT_DIR") or data.get("cwd") or os.getcwd())


def rel(path: str, root: str) -> str | None:
    """Project-relative path with '/', or None when outside the project."""
    real = os.path.realpath(path)
    if real == root:
        return ""
    if real.startswith(root + os.sep):
        return os.path.relpath(real, root).replace(os.sep, "/")
    return None


def in_list(relpath: str, entries: list[str]) -> bool:
    for e in entries:
        if e.endswith("/"):
            if relpath == e[:-1] or relpath.startswith(e):
                return True
        elif relpath == e:
            return True
    return False


def is_build_output(relpath: str) -> bool:
    return any(part in BUILD_DIRS for part in relpath.split("/"))


def outside_ok(path: str) -> bool:
    """Writes outside the project are allowed only to temporary/scratch locations."""
    real = os.path.realpath(path)
    allowed = [os.path.realpath(tempfile.gettempdir()), "/tmp", "/dev"]
    home = os.path.expanduser("~")
    allowed += [os.path.join(home, d) for d in (".dotnet", ".nuget", ".cache", ".local", ".swiftpm")]
    return any(real == a or real.startswith(a.rstrip("/") + "/") for a in allowed)


class Decision(Exception):
    def __init__(self, kind: str, reason: str):
        super().__init__(reason)
        self.kind, self.reason = kind, reason


def check_path(agent: str | None, path: str, root: str, data: dict) -> None:
    """Raise Decision if this agent may not change this path."""
    relpath = rel(path, root)
    sub = agent is not None
    if relpath is None:
        if sub and agent in NEXUS_AGENTS and not outside_ok(path):
            raise Decision("deny", f"{agent}: writing outside the project and temp folders is not allowed ({path}).")
        return
    if SECRET_FILE.search(relpath):
        raise Decision("deny" if sub else "ask", f"'{relpath}' looks like a secret/credential file; secrets never go into the repository.")
    if not sub or agent not in NEXUS_AGENTS:
        return
    if in_list(relpath, PROTECTED):
        raise Decision("deny", f"{agent}: '{relpath}' is changed only by the main session (agent config, git, FEATURES statuses).")
    if is_build_output(relpath):
        return
    if agent in READ_ONLY:
        raise Decision("deny", f"{agent} is read-only: describe the change in your report instead of editing '{relpath}'.")
    zone = ZONES[agent]
    if agent == "android-developer" and relpath.startswith("android/") and not os.path.isdir(os.path.join(root, "android")):
        raise Decision("deny", "android-developer: android/ is created only after the owner approves Android development.")
    if agent == "release-manager" and relpath == "ios/project.yml":
        return  # version-only check is done on the content
    if not in_list(relpath, zone):
        raise Decision("deny", f"{agent} may change only {', '.join(zone)}; '{relpath}' belongs to another agent. Stop and report to the main session.")


def changed_lines(old: str, new: str) -> list[str]:
    a, b = old.splitlines(), new.splitlines()
    sa, sb = set(a), set(b)
    return [l for l in a if l not in sb] + [l for l in b if l not in sa]


def check_content(agent: str | None, relpath: str | None, old: str, new: str) -> None:
    if relpath is None:
        return
    lines = changed_lines(old, new)
    sub = agent is not None
    if any(SIGNING_LINE.search(l) for l in lines):
        raise Decision("deny" if sub else "ask", f"Change touches Bundle ID / signing settings in '{relpath}' — owner approval required.")
    if not sub or agent not in NEXUS_AGENTS:
        return
    if relpath == "ios/project.yml":
        if agent == "release-manager" and not all(VERSION_LINE.search(l) for l in lines if l.strip()):
            raise Decision("deny", "release-manager may change only MARKETING_VERSION / CURRENT_PROJECT_VERSION in ios/project.yml.")
        if agent == "ios-developer" and any(VERSION_LINE.search(l) for l in lines):
            raise Decision("deny", "Version numbers are owned by release-manager.")


def read_file(path: str) -> str:
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            return f.read()
    except OSError:
        return ""


# --- Bash target extraction ---------------------------------------------------
def tokens(command: str) -> list[str]:
    lex = shlex.shlex(command, posix=True, punctuation_chars=";&|()<>")
    lex.whitespace_split = True
    lex.commenters = ""
    try:
        return list(lex)
    except ValueError:
        return command.split()


HEREDOC = re.compile(r"<<-?\s*(['\"]?)([A-Za-z_][A-Za-z0-9_]*)\1")


def strip_heredocs(command: str) -> str:
    """Remove here-document bodies: their text is data, not shell commands."""
    out, lines, i = [], command.split("\n"), 0
    while i < len(lines):
        line = lines[i]
        out.append(line)
        delims = [m.group(2) for m in HEREDOC.finditer(line)]
        i += 1
        for d in delims:
            while i < len(lines) and lines[i].strip() != d:
                i += 1
            i += 1
    return "\n".join(out)


def bash_targets(command: str, cwd: str) -> list[str]:
    """Paths a shell command would create, change or delete (heuristic)."""
    toks = tokens(strip_heredocs(command).replace("\n", " ; "))
    targets: list[str] = []
    here = cwd
    i, start = 0, True
    current: list[str] = []

    def resolve(p: str) -> str:
        p = os.path.expanduser(os.path.expandvars(p))
        return p if os.path.isabs(p) else os.path.join(here, p)

    def flush(seg: list[str]) -> None:
        nonlocal here
        # Skip wrappers (sudo, env, timeout 30, nohup ...), their flags and VAR=value assignments.
        while seg and (seg[0] in WRAPPERS or re.match(r"^[A-Za-z_][A-Za-z0-9_]*=", seg[0])
                       or (seg[0].startswith("-") or re.match(r"^\d+[smhd]?$", seg[0]))):
            seg = seg[1:]
        if not seg:
            return
        cmd, args = os.path.basename(seg[0]), [a for a in seg[1:] if not a.startswith("-")]
        if cmd == "cd" and args:
            here = resolve(args[0])
        elif cmd in FILE_COMMANDS_ALL_ARGS:
            targets.extend(resolve(a) for a in args)
        elif cmd in FILE_COMMANDS_LAST_ARG and args:
            targets.append(resolve(args[-1]))
        elif cmd in {"sed", "perl"} and any(re.match(r"^-[a-zA-Z]*i", a) for a in seg[1:]):
            targets.extend(resolve(a) for a in args if os.path.exists(resolve(a)))
        elif cmd == "unzip":
            # unzip -d DIR extracts into DIR, otherwise into the current directory.
            d = seg.index("-d") + 1 if "-d" in seg else 0
            targets.append(resolve(seg[d]) if 0 < d < len(seg) else os.path.join(here, "_extract_"))
        elif cmd == "tar":
            mode = next((a for a in seg[1:] if not a.startswith("--")), "")
            if "x" in mode.lstrip("-"):
                # tar -x writes into -C DIR / --directory=DIR, otherwise into the current directory.
                dest = here
                for k, a in enumerate(seg):
                    if a == "-C" and k + 1 < len(seg):
                        dest = resolve(seg[k + 1])
                    elif a.startswith("--directory="):
                        dest = resolve(a.split("=", 1)[1])
                targets.append(os.path.join(dest, "_extract_"))

    while i < len(toks):
        t = toks[i]
        if t in OPERATORS or t in {"&&", "||"}:
            flush(current)
            current = []
        elif t in {">", ">>", ">|", "&>", "&>>", "<>"}:
            if i + 1 < len(toks):
                nxt = toks[i + 1]
                if not (t == ">" and nxt.startswith("&")):
                    targets.append(resolve(nxt))
                i += 1
        elif t == ">&":
            # 2>&1 style duplication; ">& file" writes a file.
            if i + 1 < len(toks) and not toks[i + 1].isdigit() and toks[i + 1] != "-":
                targets.append(resolve(toks[i + 1]))
            i += 1
        elif t in {"<", "<<", "<<<", "<<-"}:
            i += 1
        else:
            current.append(t)
        i += 1
    flush(current)
    return targets


def check_bash(agent: str | None, full_command: str, root: str, cwd: str, data: dict) -> list[str]:
    sub = agent is not None
    # Here-document bodies are data unless they are fed to a shell.
    command = full_command if re.search(r"\b(ba|z|da)?sh\b[^\n]*<<", full_command) else strip_heredocs(full_command)
    for pattern, what in IRREVERSIBLE:
        if re.search(pattern, command):
            raise Decision("deny" if sub else "ask", f"Irreversible or outward-facing action ({what}) — needs the owner's explicit permission.")
    if not sub or agent not in NEXUS_AGENTS:
        return []
    if GIT_WRITE.search(command) or GIT_BRANCH_WRITE.search(command):
        raise Decision("deny", f"{agent}: subagents only read git (status/log/diff/show/blame); commits and branch changes are done by the main session.")
    if NETWORK_WRITE.search(command):
        raise Decision("deny", f"{agent}: sending data to external services is not allowed for subagents.")
    if DEPENDENCY_ADD.search(command):
        raise Decision("deny", f"{agent}: adding dependencies or regenerating project files needs owner approval (D10); report it instead.")
    if INLINE_CODE.search(full_command) and WRITE_TOKENS.search(full_command):
        raise Decision("deny", f"{agent}: inline scripts that write files or spawn processes are not allowed; use the Edit/Write tools in your zone.")
    targets = bash_targets(command, cwd)
    project_targets = []
    for t in targets:
        if "$" in t or "`" in t:
            raise Decision("deny", f"{agent}: cannot verify the write target '{t}'; use an explicit path.")
        check_path(agent, t, root, data)
        r = rel(t, root)
        if r is not None and not is_build_output(r):
            project_targets.append(t)
    if INPLACE.search(strip_heredocs(command)) and agent in READ_ONLY:
        raise Decision("deny", f"{agent} is read-only: in-place editing commands are not allowed.")
    return project_targets


# --- Single-writer lock -------------------------------------------------------
def lock_path(root: str) -> str:
    h = hashlib.sha256(root.encode()).hexdigest()[:16]
    return os.path.join(tempfile.gettempdir(), f"nexus-agent-writer-{h}.lock")


def holder_id(data: dict, agent: str) -> str:
    # Subagents carry agent_id; an agent run as the whole session (--agent) is identified by the session.
    return data.get("agent_id") or f"session:{data.get('session_id', '')}:{agent}"


def take_lock(agent: str, data: dict, root: str) -> None:
    me = holder_id(data, agent)
    path = lock_path(root)
    now = time.time()
    try:
        with open(path, encoding="utf-8") as f:
            held = json.load(f)
        if held.get("id") != me and now - float(held.get("ts", 0)) < LOCK_TTL:
            raise Decision("deny", f"Another writing agent ({held.get('agent')}) is changing project files right now. "
                                   f"Writing agents run one at a time — stop and report this to the main session.")
    except (OSError, ValueError):
        pass
    tmp = path + f".{os.getpid()}"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump({"id": me, "agent": agent, "ts": now}, f)
    os.replace(tmp, path)


def release_lock(data: dict, root: str) -> None:
    path = lock_path(root)
    try:
        with open(path, encoding="utf-8") as f:
            held = json.load(f)
    except (OSError, ValueError):
        return
    agent = data.get("agent_type")
    if agent and held.get("id") == holder_id(data, agent):
        os.remove(path)


# --- Entry point --------------------------------------------------------------
def decide(data: dict) -> tuple[str, str]:
    root = project_dir(data)
    agent = data.get("agent_type") or None
    tool = data.get("tool_name", "")
    ti = data.get("tool_input") or {}
    cwd = data.get("cwd") or root
    writes: list[str] = []
    try:
        if tool in {"Edit", "Write", "MultiEdit", "NotebookEdit"}:
            path = ti.get("file_path") or ti.get("notebook_path") or ""
            if not path:
                return "allow", ""
            path = path if os.path.isabs(path) else os.path.join(cwd, path)
            check_path(agent, path, root, data)
            relpath = rel(path, root)
            if tool == "Edit":
                old, new = ti.get("old_string", ""), ti.get("new_string", "")
            elif tool == "MultiEdit":
                edits = ti.get("edits") or []
                old = "\n".join(e.get("old_string", "") for e in edits)
                new = "\n".join(e.get("new_string", "") for e in edits)
            elif tool == "Write":
                old, new = read_file(path), ti.get("content", "")
            else:
                old, new = "", ""
            check_content(agent, relpath, old, new)
            if relpath is not None and not is_build_output(relpath):
                writes.append(path)
        elif tool == "Bash":
            writes = check_bash(agent, ti.get("command", ""), root, cwd, data)
        if agent in ZONES and writes:
            take_lock(agent, data, root)
    except Decision as d:
        return d.kind, d.reason
    return "allow", ""


def log(data: dict, kind: str, reason: str) -> None:
    try:
        with open(os.path.join(tempfile.gettempdir(), "nexus-agent-guard.log"), "a", encoding="utf-8") as f:
            f.write(json.dumps({"t": time.strftime("%Y-%m-%dT%H:%M:%S"), "agent": data.get("agent_type"),
                                "tool": data.get("tool_name"), "decision": kind, "reason": reason}, ensure_ascii=False) + "\n")
    except OSError:
        pass


def main() -> int:
    data = json.load(sys.stdin)
    if "--release" in sys.argv:
        release_lock(data, project_dir(data))
        return 0
    kind, reason = decide(data)
    if kind == "allow":
        return 0
    log(data, kind, reason)
    print(json.dumps({"hookSpecificOutput": {"hookEventName": "PreToolUse", "permissionDecision": kind,
                                             "permissionDecisionReason": "Nexus PC agent guard: " + reason}}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
