# Third-party notices

This application uses .NET 8 and Microsoft Windows App SDK / WinUI 3 through their official NuGet packages. Their original licenses and bundled notices apply; retain notices included in the self-contained runtime distribution.

- [.NET runtime](https://github.com/dotnet/runtime), MIT license and its third-party notices.
- [Windows App SDK](https://github.com/microsoft/WindowsAppSDK) and [WinUI](https://github.com/microsoft/microsoft-ui-xaml), MIT license; see the licenses accompanying the packages.
- [Windows SDK BuildTools](https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools), Microsoft license terms listed in the package. Build-time dependency.
- [DingTalk Workspace CLI](https://github.com/DingTalk-Real-AI/dingtalk-workspace-cli), Apache-2.0; independently installed by the user and not bundled. The app invokes its published command contract; no DWS implementation source is copied.

The native navigation and settings experience references the user's UPCAM application. No UPCAM or PowerToys source was copied into this repository. This notice does not license this repository's own source.

The Workbench plugin compatibility export contains exact same-owner Project Workbench SDK/Core/presenter source at commit 6565180b17130f6d494978291df4138de3fdb0b4. Provenance/attribution and original paths are retained in eng/vendor/workbench-6565180b; no additional license grant is asserted. The plugin aligns with the host WindowsAppSDK1.8.260921001 dependency; the standalone dependency pin remains unchanged.

The 0.3.1 adapter uses 35 exact inputs at Project Workbench commit 82bd958e220aaf94ccdbf1ea68b0ec53b770706a, root tree b46db5c7649628d87d365e2facc0832b5d9d4159, retained under eng/vendor/workbench-82bd958e with SHA-1/SHA-256 provenance and attribution. All imported bytes are immutable. The earlier 6565180b export remains as a rollback graph. No additional upstream license grant is asserted.
