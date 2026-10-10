"""Regenerates the bundled offline LiteLLM pricing snapshot.

TaskbarQuota downloads LiteLLM's model_prices_and_context_window.json at runtime
(refreshed daily); this snapshot is only used until the first download succeeds.

Compact keys, all USD per million tokens:
  i/o/cr/cw      standard input/output/cache-read/cache-write
  ia/oa/cra/cwa  long-context tier (whole request once input exceeds ctx, default 200K)
  ctx            long-context boundary when it is not 200K (OpenAI: 272K)
  fi/fo/fcr/fcw  priority ("fast") tier, plus fia/foa/fcra/fcwa for its long-context tier
  fast           fast-mode multiple from provider_specific_entry.fast (Claude)

Usage: python -I tools/update-litellm-snapshot.py
"""

import datetime
import json
import pathlib
import urllib.request

URL = "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json"
OUT = pathlib.Path(__file__).resolve().parent.parent / "src/TaskbarQuota.App/Resources/Pricing/pricing_litellm_snapshot.json"

FIELDS = {
    "i": "input_cost_per_token",
    "o": "output_cost_per_token",
    "cr": "cache_read_input_token_cost",
    "cw": "cache_creation_input_token_cost",
}


def per_million(value):
    return round(value * 1_000_000, 10) if isinstance(value, (int, float)) else None


def tier(entry, suffix, prefix, out):
    if per_million(entry.get(FIELDS["i"] + suffix)) is None or per_million(entry.get(FIELDS["o"] + suffix)) is None:
        return False
    for key, field in FIELDS.items():
        if (value := per_million(entry.get(field + suffix))) is not None:
            out[prefix + key] = value
    boundary = "272k" if entry.get(f"{FIELDS['i']}_above_272k_tokens{suffix}") is not None else "200k"
    for key, field in FIELDS.items():
        if (value := per_million(entry.get(f"{field}_above_{boundary}_tokens{suffix}"))) is not None:
            out[prefix + key + "a"] = value
    if boundary == "272k":
        out["ctx"] = 272_000
    return True


def main():
    with urllib.request.urlopen(URL, timeout=60) as response:
        document = json.load(response)
    models = {}
    for name, entry in sorted(document.items()):
        if not isinstance(entry, dict):
            continue
        compact = {}
        if not tier(entry, "", "", compact):
            continue
        if not tier(entry, "_priority", "f", compact):
            fast = (entry.get("provider_specific_entry") or {}).get("fast")
            if isinstance(fast, (int, float)) and fast > 0:
                compact["fast"] = fast
        models[name] = compact
    retrieved = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    OUT.write_text(json.dumps({"models": models, "retrieved_at": retrieved}, separators=(",", ":")), encoding="utf-8")
    print(f"{len(models)} models -> {OUT}")


if __name__ == "__main__":
    main()
