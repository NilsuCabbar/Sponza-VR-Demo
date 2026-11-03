"""Perceptual image quality metrics for a QualityBenchmark run.

Reads BenchmarkResults/<run>/images.csv and run.json written by the Unity benchmark and computes, for every
image against its reference:

- FovVideoVDP (Mantiuk et al., SIGGRAPH 2021): foveated quality in JOD units (10 = no visible difference),
  with the HTC Vive Pro display model and the gaze fixed at the image center, like fixed foveation.
- LPIPS (Zhang et al., CVPR 2018): learned perceptual distance, lower is better.
- FLIP (Andersson et al., HPG 2020): perceived difference when flipping between the two images, lower is better.
  Also reported inside the focus region, outside it, and on the seam between them.

Usage:
    Tools/metrics/.venv/Scripts/python Tools/metrics/compute_metrics.py [run folder]
Without an argument the newest run in BenchmarkResults is used. Writes perceptual.csv and perceptual_summary.md
into the run folder.
"""

import csv
import json
import math
import sys
import time
from importlib import metadata
from pathlib import Path

import imageio.v3 as iio
import numpy as np


def newest_run(root):
    runs = sorted(p for p in root.iterdir() if p.is_dir() and (p / "images.csv").exists())
    if not runs:
        sys.exit(f"No benchmark runs with images.csv in {root}")
    return runs[-1]


def load_rgb(path):
    image = iio.imread(path)
    return np.ascontiguousarray(image[..., :3])


def region_masks(height, width, rect, seam_width):
    """Focus, periphery and seam masks matching QualityBenchmark.RegionSsim. Images are stored top row first,
    Unity's rectangle starts at the bottom, so it is flipped vertically."""
    x, y, w, h = rect
    top = height - (y + h)
    ys, xs = np.mgrid[0:height, 0:width]
    dx = np.maximum(x - xs, xs - (x + w - 1))
    dy = np.maximum(top - ys, ys - (top + h - 1))
    outside = (dx > 0) & (dy > 0)
    distance = np.where(outside, np.sqrt(np.maximum(dx, 0) ** 2 + np.maximum(dy, 0) ** 2).astype(int), np.maximum(dx, dy))
    seam = np.abs(distance) <= seam_width // 2
    focus = (distance < 0) & ~seam
    periphery = (distance > 0) & ~seam
    return focus, periphery, seam


def main():
    project = Path(__file__).resolve().parents[2]
    run = Path(sys.argv[1]) if len(sys.argv) > 1 else newest_run(project / "BenchmarkResults")
    info = json.loads((run / "run.json").read_text())
    width, height, fov_vertical = info["width"], info["height"], info["fov_vertical"]
    rows = list(csv.DictReader(open(run / "images.csv", newline="", encoding="utf-8")))
    print(f"Run {run.name}: {len(rows)} images, {width}x{height}, vertical FOV {fov_vertical}")

    import lpips
    import pyfvvdp
    import torch
    import flip_evaluator as flip

    # The default capture (1440x1600, 93 deg vertical) is one eye of the Vive Pro, which has its own display model.
    geometry = None
    if (width, height) != (1440, 1600) or abs(fov_vertical - 93) > 0.5:
        geometry = pyfvvdp.fvvdp_display_geometry((width, height), distance_m=3, fov_vertical=fov_vertical)
    fvvdp = pyfvvdp.fvvdp(display_name="htc_vive_pro", display_geometry=geometry, foveated=True, quiet=True)
    lpips_net = lpips.LPIPS(net="alex", verbose=False)

    # Pixels per degree at the image center, for FLIP.
    tan_half_y = math.tan(math.radians(fov_vertical / 2))
    ppd = (height / 2) / tan_half_y * math.pi / 180
    focus_mask, periphery_mask, seam_mask = region_masks(height, width, info["focus_rect"], 24)

    results = []
    for i, row in enumerate(rows):
        start = time.time()
        test = load_rgb(run / row["image"])
        reference = load_rgb(run / row["reference"])

        with torch.no_grad():
            jod, _ = fvvdp.predict(test, reference, dim_order="HWC", frames_per_second=0)
            jod = float(jod)

            def to_tensor(image):
                return torch.from_numpy(image).permute(2, 0, 1)[None].float() / 127.5 - 1.0

            lpips_value = float(lpips_net(to_tensor(test), to_tensor(reference)).item())

        error_map, flip_mean, _ = flip.evaluate(reference.astype(np.float32) / 255, test.astype(np.float32) / 255, "LDR",
                                                applyMagma=False, parameters={"ppd": ppd})
        error_map = np.squeeze(error_map)
        results.append({
            "scenario_index": row["scenario_index"],
            "scenario": row["scenario"],
            "view": row["view"],
            "fvvdp_jod": jod,
            "lpips": lpips_value,
            "flip": float(flip_mean),
            "flip_focus": float(error_map[focus_mask].mean()),
            "flip_periphery": float(error_map[periphery_mask].mean()),
            "flip_seam": float(error_map[seam_mask].mean()),
        })
        print(f"[{i + 1}/{len(rows)}] {row['scenario']} / {row['view']}: JOD {jod:.2f}, LPIPS {lpips_value:.4f}, "
              f"FLIP {flip_mean:.4f} ({time.time() - start:.1f} s)")

    with open(run / "perceptual.csv", "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=list(results[0].keys()))
        writer.writeheader()
        writer.writerows(results)

    lines = [
        f"# Perceptual metrics {run.name}",
        "",
        f"- FovVideoVDP (pyfvvdp {metadata.version('pyfvvdp')}): display htc_vive_pro, foveated, gaze at the image center. JOD 10 = no visible difference; "
        "papers treat about 9 and above as acceptable.",
        f"- LPIPS (AlexNet): 0 = identical, lower is better.",
        f"- FLIP (LDR, {ppd:.1f} pixels per degree): 0 = identical, lower is better. Focus / periphery / seam use the "
        "same regions as the SSIM split in summary.md.",
        "",
        "Mean over all views; JOD (worst) is the lowest view.",
        "",
        "| Scenario | JOD | JOD (worst) | LPIPS | FLIP | FLIP focus | FLIP periphery | FLIP seam |",
        "|---|---|---|---|---|---|---|---|",
    ]
    order = []
    for r in results:
        if r["scenario"] not in order:
            order.append(r["scenario"])
    for name in order:
        group = [r for r in results if r["scenario"] == name]
        mean = lambda key: sum(r[key] for r in group) / len(group)
        lines.append(f"| {name} | {mean('fvvdp_jod'):.2f} | {min(r['fvvdp_jod'] for r in group):.2f} | {mean('lpips'):.4f} | "
                     f"{mean('flip'):.4f} | {mean('flip_focus'):.4f} | {mean('flip_periphery'):.4f} | {mean('flip_seam'):.4f} |")
    (run / "perceptual_summary.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print("\n".join(lines))


if __name__ == "__main__":
    main()
