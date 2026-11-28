"""Строка состояния: шкала заполнения контекста и подсказка, когда начинать новую сессию."""
import json, sys

def pct(d):
    cw = d.get("context_window") or {}
    p = cw.get("used_percentage")
    if isinstance(p, (int, float)):
        return float(p)
    size = cw.get("context_window_size") or 0
    u = cw.get("current_usage") or {}
    used = sum(u.get(k) or 0 for k in ("input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens"))
    return used * 100.0 / size if size and used else None

try:
    d = json.load(sys.stdin)
except Exception:
    d = {}
p = pct(d)
if p is None:
    print("Контекст: нет данных")
    sys.exit(0)
p = max(0.0, min(100.0, p))
n = 20
f = round(p / 100 * n)
bar = "█" * f + "░" * (n - f)
if p < 60:
    c, t = "32", "можно продолжать"
elif p < 80:
    c, t = "33", "закончите задание и готовьте handoff"
else:
    c, t = "31", "НАЧНИТЕ НОВУЮ СЕССИЮ"
print(f"\033[{c}mКонтекст [{bar}] {p:.0f}% — {t}\033[0m")
