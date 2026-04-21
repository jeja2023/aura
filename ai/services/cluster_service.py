# 文件：聚类算法服务（cluster_service.py） | File: Clustering service
def cluster_vectors(
    items: list[dict],
    similarity_threshold: float,
    min_points: int,
    cosine_func,
) -> tuple[list[list[int]], list[int]]:
    if not items:
        return [], []

    similarity_threshold = max(0.5, min(similarity_threshold, 0.99))
    min_points = max(1, min_points)
    total = len(items)
    labels = [None] * total
    noise = -1
    neighbors: list[list[int]] = [[] for _ in range(total)]

    for i in range(total):
        for j in range(i, total):
            score = cosine_func(items[i]["feature"], items[j]["feature"])
            if score < similarity_threshold:
                continue
            neighbors[i].append(j)
            if i != j:
                neighbors[j].append(i)

    cluster_id = 0
    for idx in range(total):
        if labels[idx] is not None:
            continue

        region = neighbors[idx]
        if len(region) < min_points:
            labels[idx] = noise
            continue

        cluster_id += 1
        labels[idx] = cluster_id
        queue = list(region)
        cursor = 0
        while cursor < len(queue):
            current = queue[cursor]
            cursor += 1
            if labels[current] == noise:
                labels[current] = cluster_id
            if labels[current] is not None:
                continue

            labels[current] = cluster_id
            current_neighbors = neighbors[current]
            if len(current_neighbors) < min_points:
                continue
            for neighbor in current_neighbors:
                if labels[neighbor] is None or labels[neighbor] == noise:
                    queue.append(neighbor)

    clusters: list[list[int]] = []
    for cid in range(1, cluster_id + 1):
        members = [idx for idx, label in enumerate(labels) if label == cid]
        if members:
            clusters.append(members)
    noise_indexes = [idx for idx, label in enumerate(labels) if label == noise]
    return clusters, noise_indexes


def compute_cluster_cohesion(
    member_indexes: list[int],
    items: list[dict],
    vector_dim: int,
    normalize_func,
    cosine_func,
) -> float:
    if not member_indexes:
        return 0.0

    centroid = [0.0] * vector_dim
    for idx in member_indexes:
        vector = items[idx]["feature"]
        for i in range(vector_dim):
            centroid[i] += vector[i]
    count = float(len(member_indexes))
    centroid = [v / count for v in centroid]
    normalized_centroid = normalize_func(centroid)
    avg_score = sum(cosine_func(items[idx]["feature"], normalized_centroid) for idx in member_indexes) / count
    return round(float(avg_score), 4)
