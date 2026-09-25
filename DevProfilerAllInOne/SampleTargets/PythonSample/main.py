"""Small standard-library target for validating Python capture."""

from __future__ import annotations

import math
import multiprocessing as mp
import time


def calculate_batch(seed: int, iterations: int) -> float:
    total = 0.0
    for index in range(iterations):
        total += math.sin(seed + index * 0.001) * math.cos(index * 0.0007)
    return total


def child_worker() -> None:
    for batch in range(4):
        calculate_batch(batch + 100, 80_000)
        time.sleep(0.08)


def main() -> None:
    mp.set_start_method("spawn", force=True)
    child = mp.Process(target=child_worker, name="ProfilerSampleChild")
    child.start()

    for batch in range(8):
        result = calculate_batch(batch, 120_000)
        print(f"Batch {batch}: {result:.4f}", flush=True)
        time.sleep(0.1)

    child.join()
    print("Python sample complete.", flush=True)


if __name__ == "__main__":
    mp.freeze_support()
    main()
