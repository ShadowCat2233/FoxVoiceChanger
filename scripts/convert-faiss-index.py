"""Convert an RVC FAISS IndexIVFFlat file to FoxVoice's runtime-only FVIX v1.

This is an import-time helper. The realtime engine reads FVIX directly and has
no Python/FAISS dependency.
"""
import struct
import sys

import faiss
import numpy as np


def main(source: str, destination: str) -> None:
    index = faiss.read_index(source)
    if not isinstance(index, faiss.IndexIVFFlat):
        raise ValueError(f"only IndexIVFFlat is supported, got {type(index).__name__}")
    dimension, list_count, vector_count = index.d, index.nlist, index.ntotal
    centroids = np.ascontiguousarray(index.quantizer.reconstruct_n(0, list_count), dtype="<f4")
    vectors = np.ascontiguousarray(index.reconstruct_n(0, vector_count), dtype="<f4")
    _, assignments = index.quantizer.search(vectors, 1)
    order = np.argsort(assignments[:, 0], kind="stable")
    sorted_vectors = vectors[order]
    counts = np.bincount(assignments[:, 0], minlength=list_count).astype("<u8")
    offsets = np.empty(list_count + 1, dtype="<u8")
    offsets[0] = 0
    np.cumsum(counts, out=offsets[1:])
    with open(destination, "wb") as output:
        output.write(b"FVIX\0\0\0\x01")
        output.write(struct.pack("<IIQ", dimension, list_count, vector_count))
        output.write(centroids.tobytes())
        output.write(offsets.tobytes())
        output.write(sorted_vectors.tobytes())
    print(f"FVIX d={dimension} nlist={list_count} ntotal={vector_count} -> {destination}")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit("usage: convert-faiss-index.py source.index destination.fvix")
    main(sys.argv[1], sys.argv[2])
