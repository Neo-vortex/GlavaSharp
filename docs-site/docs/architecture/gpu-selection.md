# GPU Selection

`GpuEnumerator.cs`

`--list-gpus` enumerates the system's DRM render nodes and prints an
indexed list of the GPUs GlavaSharp can render on:

```
Available GPUs (use --gpu <index>):
  [0] AMD (pci id 0x1002:0x73df, driver amdgpu) [card0]
  [1] Intel (pci id 0x8086:0x4680, driver i915) [card1]
```

Each entry shows the vendor, PCI device ID, kernel driver, and DRM card
node backing that index. Pass the index to `--gpu <index>` to pin
rendering to that GPU (useful on hybrid-graphics laptops where the
default render node isn't the one you want driving the visualizer).
`--gpu` affects which GPU renders the window/shader pipeline; it's
independent of `--fft-device gpu`, which only controls where the FFT
itself runs.
