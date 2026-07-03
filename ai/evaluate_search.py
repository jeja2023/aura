import argparse
import json
import logging

from services.evaluation_service import evaluate_retrieval_dataset, load_eval_dataset
from utils.vector_utils import cosine, normalize_feature


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Evaluate Aura AI retrieval quality with a labeled JSON dataset.")
    parser.add_argument("dataset_path", help="Path to a JSON dataset with gallery and queries arrays.")
    parser.add_argument("--vector-dim", type=int, default=512, help="Feature dimension used by the running model.")
    parser.add_argument("--top-k", type=int, default=10)
    parser.add_argument("--min-score", type=float, default=-1.0)
    parser.add_argument("--candidate-multiplier", type=int, default=8)
    parser.add_argument("--candidate-pool", type=int, default=0)
    parser.add_argument("--ann-probe", type=int, default=16)
    parser.add_argument("--rerank-window", type=int, default=30)
    parser.add_argument("--summary-only", action="store_true", help="Print only the summary object.")
    parser.add_argument("--min-recall", type=float, default=None, help="Fail when recall_at_k is below this value.")
    parser.add_argument("--min-mrr", type=float, default=None, help="Fail when MRR is below this value.")
    parser.add_argument("--max-empty-rate", type=float, default=None, help="Fail when empty_rate is above this value.")
    return parser


def _threshold_failures(summary: dict, args) -> list[str]:
    failures = []
    if args.min_recall is not None and float(summary.get("recall_at_k", 0.0)) < args.min_recall:
        failures.append(f"recall_at_k<{args.min_recall}")
    if args.min_mrr is not None and float(summary.get("mrr", 0.0)) < args.min_mrr:
        failures.append(f"mrr<{args.min_mrr}")
    if args.max_empty_rate is not None and float(summary.get("empty_rate", 1.0)) > args.max_empty_rate:
        failures.append(f"empty_rate>{args.max_empty_rate}")
    return failures


def main() -> int:
    args = _build_parser().parse_args()
    logging.basicConfig(level=logging.WARNING, format="%(levelname)s %(message)s")
    logger = logging.getLogger("aura.ai.eval")
    dataset = load_eval_dataset(args.dataset_path)
    result = evaluate_retrieval_dataset(
        dataset,
        top_k=args.top_k,
        min_score=args.min_score,
        candidate_multiplier=args.candidate_multiplier,
        candidate_pool=args.candidate_pool,
        ann_probe=args.ann_probe,
        rerank_window=args.rerank_window,
        normalize_feature_func=lambda feature: normalize_feature(feature, args.vector_dim),
        cosine_func=cosine,
        vector_dim=args.vector_dim,
        logger=logger,
    )
    summary = result.get("summary", {})
    failures = _threshold_failures(summary, args)
    if failures:
        summary["threshold_failures"] = failures

    payload = summary if args.summary_only else result
    print(json.dumps(payload, ensure_ascii=False, indent=2))
    return 2 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
