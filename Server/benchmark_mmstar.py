"""
Run a local MMStar-style benchmark for:
  1. Single FastVLM-0.5B
  2. Shared FastVLM-0.5B multi-agent graph

Outputs:
  Server/benchmark_outputs/mmstar_<timestamp>/
    predictions.jsonl
    summary.csv
    summary.md

The official MMStar guideline computes visual score S_v, text-only score S_wv,
MG = S_v - S_wv, and ML = max(0, S_wv - S_t). We can measure S_v and a
blank-image text-only proxy S_wv for our VLM endpoints. The base LLM text-only
score S_t is not exposed by FastVLM here, so ML is left blank unless supplied
with --base-text-score-single / --base-text-score-multi.
"""

from __future__ import annotations

import argparse
import csv
import json
import os
import re
import sys
import time
from collections import defaultdict
from dataclasses import asdict, dataclass
from datetime import datetime
from pathlib import Path
from typing import Iterable

if "--offline" in sys.argv:
    os.environ["HF_DATASETS_OFFLINE"] = "1"
    os.environ["HF_HUB_OFFLINE"] = "1"

from datasets import Dataset, load_dataset
from PIL import Image

ROOT = Path(__file__).resolve().parent
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

import server


CATEGORY_TO_COL = {
    "coarse perception": "CP",
    "fine-grained perception": "FP",
    "instance reasoning": "IR",
    "logical reasoning": "LR",
    "science & technology": "ST",
    "math": "MA",
}

MODEL_ROWS = [
    {
        "key": "single",
        "model": "FastVLM-0.5B Single",
        "llm": "FastVLM",
        "param": "0.5B",
    },
    {
        "key": "multi",
        "model": "FastVLM-0.5B Multi-Agent",
        "llm": "FastVLM shared agents",
        "param": "0.5B shared",
    },
]


@dataclass
class Prediction:
    run_id: str
    mode: str
    model_key: str
    index: int
    category: str
    category_col: str
    l2_category: str
    question: str
    expected: str
    predicted: str
    correct: bool
    answer_text: str
    latency_ms: float
    trace: list[dict]


def make_prompt(question: str) -> str:
    return (
        f"{question}\n\n"
        "Choose the best option. Return only the option letter, such as A, B, C, D, or E."
    )


def extract_choice(text: str) -> str:
    if not text:
        return ""
    cleaned = text.strip().upper()
    patterns = [
        r"^\s*([A-E])\s*$",
        r"^\s*([A-E])[\).:\-]",
        r"\bANSWER\s*[:\-]?\s*([A-E])\b",
        r"\bOPTION\s*([A-E])\b",
        r"\b([A-E])\b",
    ]
    for pattern in patterns:
        match = re.search(pattern, cleaned)
        if match:
            return match.group(1)
    return ""


def sample_dataset(dataset, limit: int | None, balanced: bool):
    total = len(dataset)
    if limit is None or limit <= 0 or limit >= total:
        return dataset
    if not balanced:
        return dataset.select(range(limit))

    buckets: dict[str, list[int]] = defaultdict(list)
    categories = dataset["category"]
    for idx, category in enumerate(categories):
        buckets[category].append(idx)

    ordered_categories = list(CATEGORY_TO_COL.keys())
    selected: list[int] = []
    per_cat = max(1, limit // max(1, len(ordered_categories)))
    for cat in ordered_categories:
        selected.extend(buckets.get(cat, [])[:per_cat])

    if len(selected) < limit:
        used = set(selected)
        for idx in range(total):
            if idx in used:
                continue
            selected.append(idx)
            if len(selected) >= limit:
                break
    return dataset.select(selected[:limit])


def blank_like(image: Image.Image) -> Image.Image:
    return Image.new("RGB", image.size, "white")


def find_cached_mmstar_arrow() -> Path | None:
    cache_root = Path(os.environ.get("HF_DATASETS_CACHE", Path.home() / ".cache" / "huggingface" / "datasets"))
    candidates = sorted(
        cache_root.glob("Lin-Chen___mm_star/val/*/*/mm_star-val.arrow"),
        key=lambda path: path.stat().st_mtime,
        reverse=True,
    )
    return candidates[0] if candidates else None


def load_mmstar_dataset(args):
    if args.local_dataset:
        local_path = Path(args.local_dataset).expanduser()
    elif args.offline:
        local_path = find_cached_mmstar_arrow()
    else:
        local_path = None

    if local_path:
        if not local_path.exists():
            raise FileNotFoundError(f"MMStar local Arrow file not found: {local_path}")
        print(f"Loading MMStar from local Arrow cache: {local_path}", flush=True)
        return Dataset.from_file(str(local_path))

    return load_dataset("Lin-Chen/MMStar", "val", split="val")


def run_single(image: Image.Image, prompt: str) -> tuple[str, float, list[dict]]:
    engine = server.get_vlm_engine()
    t0 = time.perf_counter()
    raw = engine.answer(image, prompt, max_new_tokens=12)
    latency_ms = (time.perf_counter() - t0) * 1000.0
    return raw, latency_ms, [{"agent": "single", "stage": "direct", "latency_ms": latency_ms, "error": ""}]


def run_multi(image: Image.Image, prompt: str) -> tuple[str, float, list[dict]]:
    router = server.get_vlm_router()
    t0 = time.perf_counter()
    answer, trace = router.ask(
        image=image,
        label="",
        prompt=prompt,
        task="mmstar",
        max_new_tokens=12,
        enable_critic=False,
    )
    latency_ms = (time.perf_counter() - t0) * 1000.0
    return answer, latency_ms, [asdict(item) for item in trace]


def iter_eval_modes(include_text_only: bool) -> Iterable[str]:
    yield "visual"
    if include_text_only:
        yield "text_only_proxy"


def run_benchmark(args) -> Path:
    if args.offline:
        os.environ["HF_DATASETS_OFFLINE"] = "1"
        os.environ["HF_HUB_OFFLINE"] = "1"

    run_id = datetime.now().strftime("%Y%m%d_%H%M%S")
    output_dir = Path(args.output_dir) / f"mmstar_{run_id}"
    output_dir.mkdir(parents=True, exist_ok=True)

    dataset = load_mmstar_dataset(args)
    rows = sample_dataset(dataset, args.limit, args.balanced)
    print(f"Loaded {len(rows)} MMStar examples into {output_dir}", flush=True)

    predictions_path = output_dir / "predictions.jsonl"
    with predictions_path.open("w", encoding="utf-8") as f:
        for row_num, row in enumerate(rows, start=1):
            image = row["image"].convert("RGB")
            prompt = make_prompt(row["question"])
            expected = str(row["answer"]).strip().upper()
            category = row["category"]
            category_col = CATEGORY_TO_COL.get(category, category)

            for mode in iter_eval_modes(args.text_only_proxy):
                eval_image = blank_like(image) if mode == "text_only_proxy" else image
                for model_key in args.models:
                    if model_key == "single":
                        answer_text, latency_ms, trace = run_single(eval_image, prompt)
                    elif model_key == "multi":
                        answer_text, latency_ms, trace = run_multi(eval_image, prompt)
                    else:
                        raise ValueError(f"Unknown model key: {model_key}")

                    predicted = extract_choice(answer_text)
                    pred = Prediction(
                        run_id=run_id,
                        mode=mode,
                        model_key=model_key,
                        index=int(row["index"]),
                        category=category,
                        category_col=category_col,
                        l2_category=row.get("l2_category", ""),
                        question=row["question"],
                        expected=expected,
                        predicted=predicted,
                        correct=predicted == expected,
                        answer_text=answer_text,
                        latency_ms=latency_ms,
                        trace=trace,
                    )
                    f.write(json.dumps(asdict(pred), ensure_ascii=False) + "\n")
                    f.flush()
                    print(
                        f"[{row_num}/{len(rows)}] {mode} {model_key} "
                        f"idx={pred.index} {predicted or '?'} vs {expected} "
                        f"{'OK' if pred.correct else 'NO'} {latency_ms:.0f}ms"
                    )

    write_summary(output_dir, args)
    return output_dir


def load_predictions(path: Path) -> list[dict]:
    with path.open("r", encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def accuracy(rows: list[dict]) -> float | None:
    if not rows:
        return None
    return 100.0 * sum(1 for row in rows if row["correct"]) / len(rows)


def write_summary(output_dir: Path, args) -> None:
    preds = load_predictions(output_dir / "predictions.jsonl")
    by_key: dict[tuple[str, str], list[dict]] = defaultdict(list)
    for pred in preds:
        by_key[(pred["model_key"], pred["mode"])].append(pred)

    base_scores = {
        "single": args.base_text_score_single,
        "multi": args.base_text_score_multi,
    }

    summary_rows = []
    for row_num, model_info in enumerate(MODEL_ROWS, start=1):
        model_key = model_info["key"]
        visual_rows = by_key.get((model_key, "visual"), [])
        text_rows = by_key.get((model_key, "text_only_proxy"), [])
        row = {
            "#": row_num,
            "Model": model_info["model"],
            "LLM": model_info["llm"],
            "Param.": model_info["param"],
        }

        category_scores = []
        for category_name, col in CATEGORY_TO_COL.items():
            cat_rows = [pred for pred in visual_rows if pred["category"] == category_name]
            score = accuracy(cat_rows)
            row[col] = "" if score is None else round(score, 2)
            if score is not None:
                category_scores.append(score)

        visual_score = accuracy(visual_rows)
        text_score = accuracy(text_rows)
        row["Avg."] = "" if visual_score is None else round(visual_score, 2)
        row["S_wv"] = "" if text_score is None else round(text_score, 2)
        row["MG"] = (
            ""
            if visual_score is None or text_score is None
            else round(visual_score - text_score, 2)
        )
        base_score = base_scores.get(model_key)
        row["ML"] = (
            ""
            if text_score is None or base_score is None
            else round(max(0.0, text_score - base_score), 2)
        )
        summary_rows.append(row)

    fieldnames = [
        "#",
        "Model",
        "LLM",
        "Param.",
        "CP",
        "FP",
        "IR",
        "LR",
        "ST",
        "MA",
        "Avg.",
        "S_wv",
        "MG",
        "ML",
    ]
    csv_path = output_dir / "summary.csv"
    with csv_path.open("w", encoding="utf-8", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(summary_rows)

    md_path = output_dir / "summary.md"
    with md_path.open("w", encoding="utf-8") as f:
        f.write("# MMStar FastVLM Evaluation\n\n")
        f.write(
            "Columns follow the MMStar guideline: MG = S_v - S_wv. "
            "ML requires an exposed base LLM text-only score S_t; if no base score "
            "is supplied, ML is blank.\n\n"
        )
        f.write("| " + " | ".join(fieldnames) + " |\n")
        f.write("| " + " | ".join(["---"] * len(fieldnames)) + " |\n")
        for row in summary_rows:
            f.write("| " + " | ".join(str(row.get(name, "")) for name in fieldnames) + " |\n")


def parse_args():
    parser = argparse.ArgumentParser()
    parser.add_argument("--limit", type=int, default=60, help="0 or omitted full-ish means all 1500; default is a balanced 60-item pilot.")
    parser.add_argument("--balanced", action="store_true", default=True, help="Sample evenly across MMStar top-level categories when using --limit.")
    parser.add_argument("--no-balanced", action="store_false", dest="balanced")
    parser.add_argument("--models", nargs="+", default=["single", "multi"], choices=["single", "multi"])
    parser.add_argument("--text-only-proxy", action="store_true", help="Also run blank-image mode for S_wv and MG.")
    parser.add_argument("--offline", action="store_true", help="Use cached Hugging Face dataset/model files only.")
    parser.add_argument("--local-dataset", default=None, help="Path to a cached MMStar .arrow file.")
    parser.add_argument("--output-dir", default=str(ROOT / "benchmark_outputs"))
    parser.add_argument("--base-text-score-single", type=float, default=None)
    parser.add_argument("--base-text-score-multi", type=float, default=None)
    return parser.parse_args()


if __name__ == "__main__":
    out = run_benchmark(parse_args())
    print(f"\nWrote benchmark outputs to: {out}")
