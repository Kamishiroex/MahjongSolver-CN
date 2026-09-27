"""Check repository-local Markdown links without sending any content to the network."""
from pathlib import Path
import re
import subprocess
import sys
from urllib.parse import unquote, urlsplit


def check(root):
    root = Path(root).resolve()
    if (root / '.git').exists():
        names = subprocess.check_output(['git', '-C', str(root), 'ls-files', '-z']).decode().split('\0')
    else:
        names = [str(p.relative_to(root)) for p in root.rglob('*.md')]
    failures = []
    count = 0
    for name in names:
        file = root / name
        if not name.endswith('.md') or not file.is_file():
            continue
        fenced = False
        for number, line in enumerate(file.read_text(encoding='utf-8-sig').splitlines(), 1):
            if line.lstrip().startswith(('```', '~~~')):
                fenced = not fenced
                continue
            if fenced:
                continue
            targets = re.findall(r'\]\((<[^>]+>|[^\s)]+)(?:\s+"[^"]*")?\)', line)
            definition = re.match(r'^\s*\[[^\]]+\]:\s*(\S+)', line)
            if definition:
                targets.append(definition[1])
            for target in targets:
                target = target.strip('<>')
                parsed = urlsplit(target)
                if parsed.scheme or parsed.netloc or not parsed.path:
                    continue
                count += 1
                path = unquote(parsed.path)
                dest = (root / path.lstrip('/') if path.startswith('/') else file.parent / path).resolve()
                if not dest.is_relative_to(root) or not dest.exists():
                    failures.append((name, number))
    return count, failures


if __name__ == '__main__':
    count, failures = check(Path(__file__).resolve().parents[1])
    for file, line in failures:
        print(f'{file}:{line}: broken local documentation link')
    print(f'Checked {count} local links; {len(failures)} failures. External URLs and anchors are not fetched.')
    sys.exit(bool(failures))
