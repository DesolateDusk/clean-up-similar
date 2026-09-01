# FileComparer

FileComparer 是僅限 Windows x64 的桌面工具，用來找出精確重複檔案及內容相近的圖片、影片。使用者確認後的刪除動作會移至 Windows 資源回收筒。

## 能做什麼

- **精確重複檔案**：先依檔案大小分組，再以快速雜湊縮小候選，最後以 SHA-256 確認內容完全相同。
- **相似圖片**：對 `.jpg`、`.jpeg`、`.png`、`.bmp`、`.webp`、`.gif` 建立 pHash 候選（包含旋轉與裁切），再以 ORB 特徵和 RANSAC 幾何驗證；副檔名不同但內容相近的受支援圖片仍可比對。
- **相似影片**：對支援格式擷取固定數量的影格 pHash，排除低對比影格後比較相容比例的影片。
- **快取**：將未變動檔案的雜湊、圖片指紋與影片指紋存入 SQLite，減少重複掃描的解碼與計算。

相似比對是用來產生可檢視的候選群組，不等同於檔案位元組完全相同；請在刪除前確認結果。

## 架構

```text
FileComparer.Domain/          領域模型、規則與抽象介面
FileComparer.Application/     掃描流程、比對策略與刪除協調
FileComparer.Infrastructure/  檔案系統、SQLite、OpenCV 與 Windows 實作
FileComparer.Desktop/         Avalonia UI、組合根與發行設定
FileComparer.Tests/           單元、整合、效能守門與原生相容性測試
```

相依方向為 `Desktop → Application / Infrastructure → Domain`。`Domain` 不依賴 UI、資料庫或 OpenCV；Desktop 只在組合根將介面接上實作。

## 本機資料

- 快取資料庫：`%LocalAppData%\FileComparer\Cache\file-cache.db`
- 應用程式記錄：`%LocalAppData%\FileComparer\Logs`

刪除快取資料庫只會讓下次掃描重新計算特徵，不會刪除使用者掃描的檔案。

## 開發與測試

需求：Windows x64 與由 [global.json](global.json) 固定的 .NET SDK 10。套件來源設定在 [NuGet.config](NuGet.config)。

```powershell
dotnet restore FileComparer.sln
dotnet build FileComparer.sln --configuration Release --no-restore
dotnet test FileComparer.sln --configuration Release --no-build --no-restore
dotnet run --project FileComparer.Desktop\FileComparer.Desktop.csproj
```

## 發行單一 EXE

專案提供 `win-x64-singlefile` 發行設定：它會建立自包含、壓縮的單一 EXE，不包含 PDB，並將 OpenCV 等原生函式庫設定為首次啟動時自動解壓。

```powershell
dotnet publish FileComparer.Desktop\FileComparer.Desktop.csproj `
  --configuration Release `
  -p:PublishProfile=win-x64-singlefile `
  --output artifacts\publish\win-x64
```

發行後可用下列命令確認單檔封裝能載入 OpenCV 的 pHash 原生功能：

```powershell
artifacts\publish\win-x64\FileComparer.Desktop.exe --native-smoke
```

結束碼 `0` 代表驗證成功。影片功能依賴 OpenCV／FFmpeg 原生程式碼，因此不能在保留影片解碼與比對功能的前提下排除這些內容。
