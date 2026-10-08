# GPU Support Specification for PyTorch Inference

## Overview

This document specifies GPU hardware agnostic support for PyTorch inference engines. The application leverages NVIDIA CUDA and AMD ROCm for accelerated inference, enabling subsecond calls on modern GPUs.

## Supported GPU Architectures

### NVIDIA GPUs (CUDA)
- **NVIDIA Tesla / A100 / H100** – Full CUDA support with optimized kernels for tensor cores
- **NVIDIA RTX series** – Partial support via CUDA compute capabilities 6.0+ (Ada Lovelace)
- **NVIDIA Quadro / Data Center** – Optimized for data center workloads with NVLink support
- **NVIDIA L40 / A6000** – High-bandwidth memory for large model loading

### AMD GPUs (ROCm)
- **AMD Radeon Instinct MI250/MI300** – Native ROCm support for high-performance computing
- **AMD Radeon Pro W6400/W6800** – Desktop-class GPUs with Vulkan/DirectX 12 acceleration
- **AMD RDNA 2/3** – Emerging support via ROCm 5.0+ for consumer-grade accelerators

## PyTorch GPU Implementation

### Device Detection

```python
import torch

def get_device() -> str:
    """Detect available PyTorch GPU."""
    if torch.cuda.is_available():
        return "cuda"
    elif torch.backends.rocm.is_available():
        return "rocm"
    else:
        return "cpu"
```

### Memory Management

PyTorch automatically handles GPU memory allocation. For optimal performance:

```python
import torch

# Move model to GPU
device = get_device()
model = model.to(device)

# Enable mixed precision for faster inference
with torch.cuda.amp.autocast(dtype=torch.float16):
    outputs = model(inputs)
```

### Kernel Dispatch

PyTorch automatically dispatches to the appropriate backend based on the device:

| Operation | NVIDIA (CUDA) | AMD (ROCm) | Intel (OpenCL) |
|-----------|---------------|------------|----------------|
| Matrix Multiplication | `torch.nn.functional.linear` | `torch.nn.functional.linear` | `torch.nn.functional.linear` |
| Convolution | `torch.nn.functional.conv2d` | `torch.nn.functional.conv2d` | `torch.nn.functional.conv2d` |
| Activation Functions | `torch.relu`, `torch.sigmoid` | `torch.relu`, `torch.sigmoid` | `torch.relu`, `torch.sigmoid` |

### Best Practices for PyTorch GPU Inference

1. **Use `.to('cuda')` or `.to(device)`** – Move model and inputs to GPU before inference
2. **Enable AMP (Automatic Mixed Precision)** – Reduces memory usage and speeds up inference
3. **Prevent CPU-GPU Transfer** – Keep data on GPU throughout the pipeline
4. **Batch Processing** – Process multiple samples simultaneously for throughput

```python
from torch.cuda.amp import autocast, GradScaler

scaler = GradScaler()

with autocast():
    outputs = model(inputs)
```

### Performance Tips

- **NVIDIA**: Use Tensor Cores with `torch.cuda.amp` for subsecond inference
- **AMD**: Leverage RDNA architecture with appropriate batch sizes
- **Intel**: Use oneAPI optimizations for discrete GPUs

## Configuration Options

| Option | Description | Default | Hardware Target |
|--------|-------------|---------|-----------------|
| `--device=cuda` | Force NVIDIA CUDA device | Auto-detect | NVIDIA Tensor Cores |
| `--device=rocm` | Force AMD ROCm device | Auto-detect | RDNA architecture |
| `--precision=fp16` | Mixed-precision inference | auto | Hardware-dependent |
| `--precision=bf16` | Bfloat16 inference | auto | Hardware-dependent |
| `--precision=fp8` | FP8 quantization (Hopper+) | auto | Hopper+ only |

## Testing Requirements

- **NVIDIA**: Verify subsecond inference on A100/H100 with mixed precision
- **AMD**: Validate ROCm kernel performance and memory bandwidth utilization
- **Cross-platform**: Ensure consistent behavior across GPU families

## Migration Path

1. **Backward Compatibility**: Existing CPU-only code paths remain unchanged
2. **Gradual Rollout**: Start with `--device=auto` flag for seamless transition
3. **Monitoring**: Track GPU utilization and latency metrics in production

## Conclusion

This specification enables PyTorch inference to leverage NVIDIA CUDA and AMD ROCm GPUs effectively. The HAL abstracts hardware differences while exposing architecture-specific optimizations through PyTorch's native GPU APIs.

*Document Version: 1.2*
*Last Updated: 2026-10-08*
