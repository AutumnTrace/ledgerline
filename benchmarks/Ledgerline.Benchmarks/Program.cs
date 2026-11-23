using BenchmarkDotNet.Running;

// dotnet run -c Release --project benchmarks/Ledgerline.Benchmarks -- --filter '*'
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
