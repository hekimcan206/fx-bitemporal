```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9711/25H2/2025Update/HudsonValley2)
13th Gen Intel Core i7-13620H 2.40GHz, 1 CPU, 16 logical and 10 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3


```
| Method                                           | Mean      | Error     | StdDev    | Gen0    | Gen1   | Allocated |
|------------------------------------------------- |----------:|----------:|----------:|--------:|-------:|----------:|
| &#39;parse one TCMB bulletin (XML, ~9 KB)&#39;           | 86.953 μs | 2.8007 μs | 8.2579 μs |  5.3711 | 0.2441 |  67.97 KB |
| &#39;plan one bulletin write (67 series)&#39;            | 94.682 μs | 2.4421 μs | 7.1238 μs | 17.2119 | 0.4883 | 211.02 KB |
| &#39;build + hash a 12-position revaluation report&#39;  | 14.870 μs | 0.7624 μs | 2.2120 μs |  1.2817 | 0.0305 |  15.93 KB |
| &#39;diff a 40-day timeline between two known times&#39; |  3.509 μs | 0.1299 μs | 0.3768 μs |  0.4044 |      - |   4.97 KB |
