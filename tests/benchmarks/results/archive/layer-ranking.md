# Layer Ranking results (retrieval quality, budget-independent)

Query top 50, depth 2, budget 4000000 (large enough that packing never truncates). PRs: 24.
recall@k and MRR/nDCG score the ranked seed-plus-expansion list before any budget cut, so they isolate
ranking quality from packing. Compare recall@k here with layer2a recall@budget: a truth file that ranks
high (good recall@k) but misses at budget is a packing loss; one that ranks low is a ranking loss.

| Mode | recall@1 | recall@3 | recall@5 | recall@10 | recall@20 | MRR | nDCG@10 | N |
|------|------:|------:|------:|------:|------:|----:|--------:|--:|
| focus | 33% | 43% | 48% | 58% | 60% | 0.979 | 0.622 | 24 |
| query | 16% | 33% | 39% | 42% | 49% | 0.545 | 0.411 | 24 |
