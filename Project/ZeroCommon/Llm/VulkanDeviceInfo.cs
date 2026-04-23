namespace Agent.Common.Llm;

public sealed record VulkanDeviceInfo(
    int Index,
    string Name,
    bool IsDiscrete,
    uint VendorId)
{
    public string VendorName => VendorId switch
    {
        0x10DE => "NVIDIA",
        0x1002 => "AMD",
        0x8086 => "Intel",
        0x106B => "Apple",
        0x13B5 => "ARM",
        _ => $"0x{VendorId:X4}"
    };

    public override string ToString()
        => $"GPU{Index}: {Name} ({VendorName}, {(IsDiscrete ? "discrete" : "integrated")})";
}
