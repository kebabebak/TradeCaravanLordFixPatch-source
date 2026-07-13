# TradeCaravanLordFixPatch — build kit

Ready-to-use patch is here: https://github.com/kebabebak/HSK-Trade-Caravan-Lord-Fix-Patch

Files to compile `TradeCaravanLordFixPatch.dll` for RimWorld HSK 1.5.

## Requirements

- [.NET SDK](https://dotnet.microsoft.com/download) (builds `net48`)
- Harmony and RimWorld refs from NuGet (`Lib.Harmony`, `Krafs.Rimworld.Ref`)

## Build

```powershell
dotnet build TradeCaravanLordFixPatch.csproj -c Release
```

Output: `out\TradeCaravanLordFixPatch.dll`
