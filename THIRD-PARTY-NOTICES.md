# 第三方声明（Third-Party Notices）

ScottPlotEx 本身以 MIT 许可发布，见 [LICENSE](LICENSE)。本文件列出本项目使用的第三方软件、它们的许可与版权声明。

## 一、本仓库中改写自第三方源码的文件

| 文件 | 来源 | 改动 | 许可 |
|---|---|---|---|
| `src/ScottPlotEx.Controls/DpiAwareMouseDragPan.cs` | [ScottPlot](https://github.com/ScottPlot/ScottPlot) 5.1.59 的 `ScottPlot.Interactivity.UserActionResponses.MouseDragPan` 与 `MouseAxisManipulation.DragPan` | 去掉高 DPI 下重复的一次 “除以 `ScaleFactor`” | MIT（ScottPlot） |

该文件包含的 ScottPlot 代码适用以下许可：

```
MIT License

Copyright (c) 2018 Scott Harden / Harden Technologies, LLC

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## 二、通过 NuGet 引用的依赖

下列包**不包含在本仓库中**，生成时由 NuGet 还原（控件库、演示程序与 `tools/ScottPlotEx.SoakTest` 的依赖相同，都来自 `ScottPlot.WinForms`）。
**分发编译后的程序**（例如把演示程序或使用本控件的应用打包给别人）时，生成目录里会带上这些程序集，需要同时随附它们的许可声明。

| 包 | 版本 | 许可 | 版权 |
|---|---|---|---|
| ScottPlot、ScottPlot.WinForms | 5.1.59 | MIT | Copyright (c) 2018 Scott Harden / Harden Technologies, LLC |
| SkiaSharp、SkiaSharp.HarfBuzz、SkiaSharp.Views.Desktop.Common、SkiaSharp.Views.WindowsForms、SkiaSharp.NativeAssets.Win32、SkiaSharp.NativeAssets.Linux.NoDependencies | 3.119.0 | MIT | Copyright (c) 2015-2016 Xamarin, Inc.<br>Copyright (c) 2017-2018 Microsoft Corporation. |
| HarfBuzzSharp、HarfBuzzSharp.NativeAssets.Win32、HarfBuzzSharp.NativeAssets.Linux | 8.3.1.1 | MIT | Copyright (c) 2015-2016 Xamarin, Inc.<br>Copyright (c) 2017-2018 Microsoft Corporation. |
| OpenTK、OpenTK.Core、OpenTK.Mathematics、OpenTK.Graphics、OpenTK.Compute、OpenTK.Input、OpenTK.Audio.OpenAL、OpenTK.Windowing.Common、OpenTK.Windowing.Desktop、OpenTK.Windowing.GraphicsLibraryFramework | 4.8.2 | MIT | Copyright (c) 2006 - 2020 Stefanos Apostolopoulos &lt;stapostol@gmail.com&gt; for the Open Toolkit library. |
| OpenTK.GLControl | 4.0.1 | MIT | Copyright (c) 2024 Team OpenTK |
| OpenTK.redist.glfw（GLFW 原生库） | 3.3.8.39 | zlib | Copyright (c) 2002-2006 Marcus Geelnard<br>Copyright (c) 2006-2019 Camilla Löwy |
| System.Runtime.CompilerServices.Unsafe | 5.0.0 | MIT | Copyright (c) .NET Foundation and Contributors |

补充说明：

- **ScottPlot 内含的第三方代码**：ScottPlot 自身包含若干需要署名的第三方代码，声明列在 ScottPlot NuGet 包的
  `Notices/NOTICES.txt` 中（本机位于 `%USERPROFILE%\.nuget\packages\scottplot\5.1.59\Notices\`）。
- **Skia、HarfBuzz 等原生库**：`SkiaSharp.NativeAssets.*` 与 `HarfBuzzSharp.NativeAssets.*` 中的原生库（libSkiaSharp、libHarfBuzzSharp）
  包含 Skia、HarfBuzz 等项目的代码，适用 BSD、MIT 等多种许可，完整声明见这些包根目录下的 `THIRD-PARTY-NOTICES.txt`。
  分发程序时请一并附上。
- **OpenTK 与 GLFW**：由 `ScottPlot.WinForms` 传递引入（仅 ScottPlot 的 `FormsPlotGL` 使用），本控件并不使用，但默认会被复制到生成目录。
- **仅测试项目使用**（不会进入分发的程序）：xunit 2.9.3 与 xunit.runner.visualstudio 2.8.2（Apache-2.0），Microsoft.NET.Test.Sdk 17.12.0（MIT）。

### MIT 许可全文

上表中标为 MIT 的各包，在各自的版权声明之下适用以下许可文本：

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### zlib 许可全文（GLFW）

```
Copyright (c) 2002-2006 Marcus Geelnard

Copyright (c) 2006-2019 Camilla Löwy

This software is provided 'as-is', without any express or implied
warranty. In no event will the authors be held liable for any damages
arising from the use of this software.

Permission is granted to anyone to use this software for any purpose,
including commercial applications, and to alter it and redistribute it
freely, subject to the following restrictions:

1. The origin of this software must not be misrepresented; you must not
   claim that you wrote the original software. If you use this software
   in a product, an acknowledgment in the product documentation would
   be appreciated but is not required.

2. Altered source versions must be plainly marked as such, and must not
   be misrepresented as being the original software.

3. This notice may not be removed or altered from any source
   distribution.
```
