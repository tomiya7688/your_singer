from __future__ import annotations

from dataclasses import dataclass

import numpy as np
import torch
import torchaudio
from sklearn.cluster import AgglomerativeClustering
from sklearn.metrics.pairwise import cosine_distances
from speechbrain.inference.speaker import EncoderClassifier


@dataclass(frozen=True)
class SegmentEmbedding:
    segment_id: str
    duration_sec: float
    vector: np.ndarray


_classifier: EncoderClassifier | None = None


def analyze_speakers(payload: dict) -> dict:
    stage_version = str(payload["stage_version"])
    segments = list(payload.get("segments") or [])

    if not segments:
        return {
            "stage_version": stage_version,
            "speakers": [],
            "assignments": [],
            "merge_history": [],
            "excluded_segment_ids": [],
        }

    embeddings = [
        _extract_embedding(
            str(item["segment_id"]),
            str(item["audio_path"]),
            float(item["duration_sec"]),
        )
        for item in segments
    ]

    matrix = np.stack([item.vector for item in embeddings])

    if len(embeddings) == 1:
        labels = np.array([0], dtype=np.int32)
    else:
        clustering = AgglomerativeClustering(
            n_clusters=None,
            metric="cosine",
            linkage="average",
            distance_threshold=0.30,
        )
        labels = clustering.fit_predict(matrix)

    speakers: list[dict] = []
    assignments: list[dict] = []

    for label in sorted(set(int(value) for value in labels)):
        indices = [i for i, value in enumerate(labels) if int(value) == label]
        speaker_id = f"spk_{label + 1:02d}"
        vectors = matrix[indices]
        centroid = vectors.mean(axis=0)
        centroid_norm = np.linalg.norm(centroid)
        if centroid_norm > 0:
            centroid = centroid / centroid_norm

        durations = [embeddings[i].duration_sec for i in indices]
        total_duration = float(sum(durations))
        distances = cosine_distances(vectors, centroid.reshape(1, -1)).reshape(-1)
        representative_indices = [
            indices[index]
            for index in np.argsort(distances)[: min(3, len(indices))]
        ]

        speakers.append(
            {
                "speaker_id": speaker_id,
                "display_name": f"話者 {label + 1}",
                "representative_segment_ids": [
                    embeddings[index].segment_id
                    for index in representative_indices
                ],
                "total_duration_sec": total_duration,
                "usable_duration_sec": total_duration,
                "embedding_centroid": centroid.astype(float).tolist(),
                "merged_from": [],
                "user_selected": False,
            }
        )

        for local_index, global_index in enumerate(indices):
            similarity = 1.0 - float(distances[local_index])
            assignments.append(
                {
                    "segment_id": embeddings[global_index].segment_id,
                    "speaker_id": speaker_id,
                    "confidence": max(0.0, min(1.0, similarity)),
                }
            )

    return {
        "stage_version": stage_version,
        "speakers": speakers,
        "assignments": assignments,
        "merge_history": [],
        "excluded_segment_ids": [],
    }


def _extract_embedding(
    segment_id: str,
    audio_path: str,
    duration_sec: float,
) -> SegmentEmbedding:
    classifier = _get_classifier()

    waveform, sample_rate = torchaudio.load(audio_path)

    if waveform.ndim == 2:
        waveform = waveform.mean(dim=0, keepdim=True)

    if sample_rate != 16000:
        waveform = torchaudio.functional.resample(
            waveform,
            sample_rate,
            16000,
        )

    if waveform.shape[-1] < 16000:
        pad = 16000 - waveform.shape[-1]
        waveform = torch.nn.functional.pad(waveform, (0, pad))

    with torch.inference_mode():
        embedding = classifier.encode_batch(waveform)

    vector = embedding.squeeze().detach().cpu().numpy().astype(np.float32)
    norm = np.linalg.norm(vector)
    if norm > 0:
        vector = vector / norm

    return SegmentEmbedding(
        segment_id=segment_id,
        duration_sec=duration_sec,
        vector=vector,
    )


def _get_classifier() -> EncoderClassifier:
    global _classifier

    if _classifier is None:
        device = "cuda" if torch.cuda.is_available() else "cpu"
        _classifier = EncoderClassifier.from_hparams(
            source="speechbrain/spkrec-ecapa-voxceleb",
            run_opts={"device": device},
        )

    return _classifier
