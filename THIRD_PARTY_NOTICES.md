# Third-party notices

This application uses .NET 8 and Microsoft Windows App SDK / WinUI 3 through their official NuGet packages. Their original licenses and bundled notices apply; retain notices included in the self-contained runtime distribution.

- [.NET runtime](https://github.com/dotnet/runtime), MIT license and its third-party notices.
- [Windows App SDK](https://github.com/microsoft/WindowsAppSDK) and [WinUI](https://github.com/microsoft/microsoft-ui-xaml), MIT license; see the licenses accompanying the packages.
- [Windows SDK BuildTools](https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools), Microsoft license terms listed in the package. Build-time dependency.
- [DingTalk Workspace CLI](https://github.com/DingTalk-Real-AI/dingtalk-workspace-cli), Apache-2.0; independently installed by the user and not bundled. The app invokes its published command contract; no DWS implementation source is copied.

The native navigation and settings experience references the user's UPCAM application. No UPCAM or PowerToys source was copied into this repository. This notice does not license this repository's own source.

The Workbench plugin compatibility export contains exact same-owner Project Workbench SDK/Core/presenter source at commit 6565180b17130f6d494978291df4138de3fdb0b4. Provenance/attribution and original paths are retained in eng/vendor/workbench-6565180b; no additional license grant is asserted. The plugin aligns with the host WindowsAppSDK1.8.260921001 dependency; the standalone dependency pin remains unchanged.

The 0.3.1 adapter uses 35 exact inputs at Project Workbench commit 82bd958e220aaf94ccdbf1ea68b0ec53b770706a, root tree b46db5c7649628d87d365e2facc0832b5d9d4159, retained under eng/vendor/workbench-82bd958e with SHA-1/SHA-256 provenance and attribution. All imported bytes are immutable. The earlier 6565180b export remains as a rollback graph. No additional upstream license grant is asserted.

The 0.3.2 adapter uses 44 exact SDK/Core/original-App inputs at Project Workbench commit 27154465c08d132798c8ea7b637f137f7340a90b, root tree 5d00a28c12e85c1bf0f77139c7c6524748c13223. The full provenance and attribution are under eng/vendor/workbench-27154465. These inputs include original host startup replay and native task/credential/package management; no upstream bytes were patched. Public SDK contracts and assembly identity are unchanged. Both earlier snapshots remain for rollback. No additional upstream license grant is asserted.

## Unicode case-folding data

The application embeds the unmodified Unicode CaseFolding-15.1.0 data file. Its original header and accompanying license are in `src/DingTalkInsights.Core/Unicode/`; this notice is also distributed with both packages.

CaseFolding-15.1.0.txt: Copyright © 2023 Unicode, Inc. The Unicode data file is unmodified.

COPYRIGHT AND PERMISSION NOTICE

Copyright © 1991-2005 Unicode, Inc. All rights reserved.
Distributed under the Terms of Use in http://www.unicode.org/copyright.html.

Permission is hereby granted, free of charge, to any person obtaining a copy
of the Unicode data files and any associated documentation (the "Data Files")
or Unicode software and any associated documentation (the "Software") to deal
in the Data Files or Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, and/or sell copies
 of the Data Files or Software, and to permit persons to whom the Data Files
or Software are furnished to do so, provided that (a) the above copyright notice(s)
and this permission notice appear with all copies of the Data Files or Software,
(b) both the above copyright notice(s) and this permission notice appear
in associated documentation, and (c) there is clear notice in each modified
Data File or in the Software as well as in the documentation associated with
the Data File(s) or Software that the data or software has been modified.

THE DATA FILES AND SOFTWARE ARE PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT OF THIRD PARTY RIGHTS.
IN NO EVENT SHALL THE COPYRIGHT HOLDER OR HOLDERS INCLUDED IN THIS NOTICE BE LIABLE
 FOR ANY CLAIM, OR ANY SPECIAL INDIRECT OR CONSEQUENTIAL DAMAGES, OR ANY DAMAGES
WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF
CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION
WITH THE USE OR PERFORMANCE OF THE DATA FILES OR SOFTWARE.

Except as contained in this notice, the name of a copyright holder shall not be used
 in advertising or otherwise to promote the sale, use or other dealings in these
Data Files or Software without prior written authorization of the copyright holder.

Unicode and the Unicode logo are trademarks of Unicode, Inc., and may be registered
 in some jurisdictions. All other trademarks and registered trademarks mentioned
herein are the property of their respective owners.
