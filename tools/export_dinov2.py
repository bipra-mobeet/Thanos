from __future__ import annotations

import hashlib
from pathlib import Path

import torch


class DinoEmbedding(torch.nn.Module):
    def __init__(self) -> None:
        super().__init__()
        self.model = torch.hub.load("facebookresearch/dinov2", "dinov2_vits14").eval()

    def forward(self, image: torch.Tensor) -> torch.Tensor:
        return self.model.forward_features(image)["x_norm_clstoken"]


def main() -> None:
    target = Path(__file__).resolve().parents[1] / "models" / "dinov2-small.onnx"
    target.parent.mkdir(parents=True, exist_ok=True)
    model = DinoEmbedding().eval()
    sample = torch.zeros(1, 3, 224, 224)
    torch.onnx.export(
        model,
        sample,
        target,
        input_names=["pixel_values"],
        output_names=["embedding"],
        opset_version=17,
        dynamo=False,
    )
    print(f"{target}  sha256={hashlib.sha256(target.read_bytes()).hexdigest()}")


if __name__ == "__main__":
    main()
