namespace EasyContainerManager.Services

open System
open System.Runtime.InteropServices
open System.Text
open Avalonia
open Avalonia.Media.Imaging
open Avalonia.Platform

/// <summary>ISO/IEC 18004 Level M（Version 1〜10）の QR コードを生成し WriteableBitmap へ描画するモジュール。</summary>
module QrCodeGenerator =

    type private VersionSpec =
        { Version: int
          TotalDataCodewords: int
          EcCodewordsPerBlock: int
          Group1Blocks: int
          Group1DataCodewords: int
          Group2Blocks: int
          Group2DataCodewords: int
          AlignmentCoords: int array }

    let private versionSpecs: VersionSpec array =
        [| { Version = 1; TotalDataCodewords = 16; EcCodewordsPerBlock = 10; Group1Blocks = 1; Group1DataCodewords = 16; Group2Blocks = 0; Group2DataCodewords = 0; AlignmentCoords = [||] }
           { Version = 2; TotalDataCodewords = 28; EcCodewordsPerBlock = 16; Group1Blocks = 1; Group1DataCodewords = 28; Group2Blocks = 0; Group2DataCodewords = 0; AlignmentCoords = [| 6; 18 |] }
           { Version = 3; TotalDataCodewords = 44; EcCodewordsPerBlock = 26; Group1Blocks = 1; Group1DataCodewords = 44; Group2Blocks = 0; Group2DataCodewords = 0; AlignmentCoords = [| 6; 22 |] }
           { Version = 4; TotalDataCodewords = 64; EcCodewordsPerBlock = 18; Group1Blocks = 2; Group1DataCodewords = 32; Group2Blocks = 0; Group2DataCodewords = 0; AlignmentCoords = [| 6; 26 |] }
           { Version = 5; TotalDataCodewords = 86; EcCodewordsPerBlock = 24; Group1Blocks = 2; Group1DataCodewords = 43; Group2Blocks = 0; Group2DataCodewords = 0; AlignmentCoords = [| 6; 30 |] }
           { Version = 6; TotalDataCodewords = 108; EcCodewordsPerBlock = 16; Group1Blocks = 4; Group1DataCodewords = 27; Group2Blocks = 0; Group2DataCodewords = 0; AlignmentCoords = [| 6; 34 |] }
           { Version = 7; TotalDataCodewords = 124; EcCodewordsPerBlock = 18; Group1Blocks = 4; Group1DataCodewords = 31; Group2Blocks = 0; Group2DataCodewords = 0; AlignmentCoords = [| 6; 22; 38 |] }
           { Version = 8; TotalDataCodewords = 154; EcCodewordsPerBlock = 22; Group1Blocks = 2; Group1DataCodewords = 38; Group2Blocks = 2; Group2DataCodewords = 39; AlignmentCoords = [| 6; 24; 42 |] }
           { Version = 9; TotalDataCodewords = 182; EcCodewordsPerBlock = 22; Group1Blocks = 3; Group1DataCodewords = 36; Group2Blocks = 2; Group2DataCodewords = 37; AlignmentCoords = [| 6; 26; 46 |] }
           { Version = 10; TotalDataCodewords = 216; EcCodewordsPerBlock = 26; Group1Blocks = 4; Group1DataCodewords = 43; Group2Blocks = 1; Group2DataCodewords = 44; AlignmentCoords = [| 6; 28; 50 |] } |]

    // GF(256) with primitive polynomial 0x11D
    let private gfExp, private gfLog =
        let exp = Array.zeroCreate<int> 512
        let log = Array.zeroCreate<int> 256
        let mutable v = 1
        for i = 0 to 254 do
            exp.[i] <- v
            log.[v] <- i
            v <- v <<< 1
            if v >= 256 then
                v <- v ^^^ 0x11D
        for i = 255 to 511 do
            exp.[i] <- exp.[i - 255]
        exp, log

    let private gfMul (a: int) (b: int) : int =
        if a = 0 || b = 0 then 0
        else gfExp.[gfLog.[a] + gfLog.[b]]

    let private rsGeneratorPoly (ecCount: int) : int array =
        let mutable poly = [| 1 |]
        for i = 0 to ecCount - 1 do
            let next = Array.zeroCreate<int> (poly.Length + 1)
            let factor = gfExp.[i]
            for j = 0 to poly.Length - 1 do
                next.[j] <- next.[j] ^^^ poly.[j]
                next.[j + 1] <- next.[j + 1] ^^^ gfMul poly.[j] factor
            poly <- next
        poly

    let private computeReedSolomonEcc (data: byte array) (ecCount: int) : byte array =
        let gen = rsGeneratorPoly ecCount
        let work = Array.zeroCreate<int> (data.Length + ecCount)
        for i = 0 to data.Length - 1 do
            work.[i] <- int data.[i]
        for i = 0 to data.Length - 1 do
            let factor = work.[i]
            if factor <> 0 then
                for j = 0 to ecCount do
                    work.[i + j] <- work.[i + j] ^^^ gfMul gen.[j] factor
        Array.init ecCount (fun i -> byte work.[data.Length + i])

    let private charCountBits (version: int) : int =
        if version <= 9 then 8 else 16

    let private selectVersion (byteCount: int) : VersionSpec =
        versionSpecs
        |> Array.tryFind (fun spec ->
            let requiredBits = 4 + charCountBits spec.Version + byteCount * 8
            requiredBits <= spec.TotalDataCodewords * 8)
        |> Option.defaultWith (fun () ->
            invalidArg "text" (sprintf "QR payload exceeds max length (213 bytes): %d bytes" byteCount))

    let private encodeDataCodewords (payload: byte array) (spec: VersionSpec) : byte array =
        let totalBits = spec.TotalDataCodewords * 8
        let bits = ResizeArray<int>(totalBits)
        let pushBits value count =
            for shift = count - 1 downto 0 do
                bits.Add((value >>> shift) &&& 1)

        // Byte mode indicator: 0100
        pushBits 0b0100 4
        pushBits payload.Length (charCountBits spec.Version)
        for b in payload do
            pushBits (int b) 8

        let term = min 4 (totalBits - bits.Count)
        for _ = 1 to term do
            bits.Add(0)

        while bits.Count % 8 <> 0 do
            bits.Add(0)

        let codewords = Array.zeroCreate<byte> spec.TotalDataCodewords
        let dataBytesCount = bits.Count / 8
        for i = 0 to dataBytesCount - 1 do
            let mutable v = 0
            for b = 0 to 7 do
                v <- (v <<< 1) ||| bits.[i * 8 + b]
            codewords.[i] <- byte v

        let mutable padIdx = 0
        for i = dataBytesCount to spec.TotalDataCodewords - 1 do
            codewords.[i] <- if padIdx % 2 = 0 then 0xECuy else 0x11uy
            padIdx <- padIdx + 1
        codewords

    let private interleaveBlocks (dataCodewords: byte array) (spec: VersionSpec) : byte array =
        let totalBlocks = spec.Group1Blocks + spec.Group2Blocks
        let blocksData = Array.zeroCreate<byte array> totalBlocks
        let blocksEcc = Array.zeroCreate<byte array> totalBlocks

        let mutable offset = 0
        for b = 0 to totalBlocks - 1 do
            let blockLen =
                if b < spec.Group1Blocks then spec.Group1DataCodewords
                else spec.Group2DataCodewords
            let slice = Array.sub dataCodewords offset blockLen
            blocksData.[b] <- slice
            blocksEcc.[b] <- computeReedSolomonEcc slice spec.EcCodewordsPerBlock
            offset <- offset + blockLen

        let maxDataLen = max spec.Group1DataCodewords spec.Group2DataCodewords
        let result = ResizeArray<byte>(spec.TotalDataCodewords + totalBlocks * spec.EcCodewordsPerBlock)
        for col = 0 to maxDataLen - 1 do
            for b = 0 to totalBlocks - 1 do
                if col < blocksData.[b].Length then
                    result.Add(blocksData.[b].[col])

        for col = 0 to spec.EcCodewordsPerBlock - 1 do
            for b = 0 to totalBlocks - 1 do
                result.Add(blocksEcc.[b].[col])

        result.ToArray()

    let private applyMaskCondition (mask: int) (x: int) (y: int) : bool =
        match mask with
        | 0 -> (y + x) % 2 = 0
        | 1 -> y % 2 = 0
        | 2 -> x % 3 = 0
        | 3 -> (y + x) % 3 = 0
        | 4 -> (y / 2 + x / 3) % 2 = 0
        | 5 -> (y * x) % 2 + (y * x) % 3 = 0
        | 6 -> ((y * x) % 2 + (y * x) % 3) % 2 = 0
        | _ -> ((y + x) % 2 + (y * x) % 3) % 2 = 0

    let private drawFormatBits (matrix: bool[,]) (size: int) (mask: int) =
        // ECC Level M indicator = 00
        let data = (0b00 <<< 3) ||| (mask &&& 0b111)
        let mutable rem = data <<< 10
        for i = 4 downto 0 do
            if ((rem >>> (i + 10)) &&& 1) <> 0 then
                rem <- rem ^^^ (0x537 <<< i)
        let bits = ((data <<< 10) ||| rem) ^^^ 0x5412

        let bitAt i = ((bits >>> i) &&& 1) <> 0

        for i = 0 to 5 do
            matrix.[i, 8] <- bitAt i
        matrix.[7, 8] <- bitAt 6
        matrix.[8, 8] <- bitAt 7
        matrix.[8, 7] <- bitAt 8
        for i = 9 to 14 do
            matrix.[8, 14 - i] <- bitAt i

        for i = 0 to 7 do
            matrix.[8, size - 1 - i] <- bitAt i
        for i = 8 to 14 do
            matrix.[size - 15 + i, 8] <- bitAt i

    let private drawVersionBits (matrix: bool[,]) (size: int) (version: int) =
        if version >= 7 then
            let mutable rem = version
            for _ = 0 to 11 do
                rem <- (rem <<< 1) ^^^ (((rem >>> 11) &&& 1) * 0x1F25)
            let bits = (version <<< 12) ||| rem
            for i = 0 to 17 do
                let bit = ((bits >>> i) &&& 1) <> 0
                let a = size - 11 + (i % 3)
                let b = i / 3
                matrix.[b, a] <- bit
                matrix.[a, b] <- bit

    let private calculatePenalty (matrix: bool[,]) (size: int) : int =
        let mutable penalty = 0

        for y = 0 to size - 1 do
            let mutable runColor = matrix.[y, 0]
            let mutable runLen = 1
            for x = 1 to size - 1 do
                if matrix.[y, x] = runColor then
                    runLen <- runLen + 1
                else
                    if runLen >= 5 then penalty <- penalty + 3 + (runLen - 5)
                    runColor <- matrix.[y, x]
                    runLen <- 1
            if runLen >= 5 then penalty <- penalty + 3 + (runLen - 5)

        for x = 0 to size - 1 do
            let mutable runColor = matrix.[0, x]
            let mutable runLen = 1
            for y = 1 to size - 1 do
                if matrix.[y, x] = runColor then
                    runLen <- runLen + 1
                else
                    if runLen >= 5 then penalty <- penalty + 3 + (runLen - 5)
                    runColor <- matrix.[y, x]
                    runLen <- 1
            if runLen >= 5 then penalty <- penalty + 3 + (runLen - 5)

        for y = 0 to size - 2 do
            for x = 0 to size - 2 do
                let c = matrix.[y, x]
                if matrix.[y, x + 1] = c && matrix.[y + 1, x] = c && matrix.[y + 1, x + 1] = c then
                    penalty <- penalty + 3

        let isFinderPat (getAt: int -> bool) (idx: int) =
            let p1 =
                getAt idx && not (getAt (idx + 1)) && getAt (idx + 2) && getAt (idx + 3) && getAt (idx + 4)
                && not (getAt (idx + 5)) && getAt (idx + 6)
                && not (getAt (idx + 7)) && not (getAt (idx + 8)) && not (getAt (idx + 9)) && not (getAt (idx + 10))
            let p2 =
                not (getAt idx) && not (getAt (idx + 1)) && not (getAt (idx + 2)) && not (getAt (idx + 3))
                && getAt (idx + 4) && not (getAt (idx + 5)) && getAt (idx + 6) && getAt (idx + 7)
                && getAt (idx + 8) && not (getAt (idx + 9)) && getAt (idx + 10)
            p1 || p2

        for y = 0 to size - 1 do
            for x = 0 to size - 11 do
                if isFinderPat (fun i -> matrix.[y, i]) x then
                    penalty <- penalty + 40

        for x = 0 to size - 1 do
            for y = 0 to size - 11 do
                if isFinderPat (fun i -> matrix.[i, x]) y then
                    penalty <- penalty + 40

        let mutable darkCount = 0
        for y = 0 to size - 1 do
            for x = 0 to size - 1 do
                if matrix.[y, x] then darkCount <- darkCount + 1
        let total = size * size
        let k = abs (darkCount * 20 - total * 10) / total
        penalty <- penalty + k * 10
        penalty

    /// <summary>UTF-8 文字列を ISO/IEC 18004 QR マトリクス（[row, col]）へエンコードする。</summary>
    let encodeText (text: string) : bool[,] =
        let safeText = if isNull text then "" else text
        let payload = Encoding.UTF8.GetBytes(safeText)
        let spec = selectVersion payload.Length
        let dataCw = encodeDataCodewords payload spec
        let allCw = interleaveBlocks dataCw spec

        let size = spec.Version * 4 + 17
        let modules = Array2D.zeroCreate<bool> size size
        let isFunction = Array2D.zeroCreate<bool> size size

        let setFunc x y dark =
            if x >= 0 && x < size && y >= 0 && y < size then
                modules.[y, x] <- dark
                isFunction.[y, x] <- true

        let drawFinder ox oy =
            for dy = -1 to 7 do
                for dx = -1 to 7 do
                    let dark =
                        if dx = -1 || dx = 7 || dy = -1 || dy = 7 then false
                        elif dx = 0 || dx = 6 || dy = 0 || dy = 6 then true
                        elif dx >= 2 && dx <= 4 && dy >= 2 && dy <= 4 then true
                        else false
                    setFunc (ox + dx) (oy + dy) dark

        drawFinder 0 0
        drawFinder (size - 7) 0
        drawFinder 0 (size - 7)

        for i = 8 to size - 9 do
            let dark = (i % 2 = 0)
            setFunc i 6 dark
            setFunc 6 i dark

        let coords = spec.AlignmentCoords
        if coords.Length > 0 then
            let lastIdx = coords.Length - 1
            for iy = 0 to lastIdx do
                for ix = 0 to lastIdx do
                    let skipCorner =
                        (ix = 0 && iy = 0) || (ix = lastIdx && iy = 0) || (ix = 0 && iy = lastIdx)
                    if not skipCorner then
                        let cx = coords.[ix]
                        let cy = coords.[iy]
                        for dy = -2 to 2 do
                            for dx = -2 to 2 do
                                let dist = max (abs dx) (abs dy)
                                setFunc (cx + dx) (cy + dy) (dist <> 1)

        setFunc 8 (size - 8) true

        for i = 0 to 5 do
            setFunc 8 i false
        setFunc 8 7 false
        setFunc 8 8 false
        setFunc 7 8 false
        for i = 9 to 14 do
            setFunc (14 - i) 8 false
        for i = 0 to 7 do
            setFunc (size - 1 - i) 8 false
        for i = 8 to 14 do
            setFunc 8 (size - 15 + i) false

        if spec.Version >= 7 then
            for i = 0 to 17 do
                let a = size - 11 + (i % 3)
                let b = i / 3
                isFunction.[b, a] <- true
                isFunction.[a, b] <- true
            drawVersionBits modules size spec.Version

        let totalBits = allCw.Length * 8
        let mutable bitIdx = 0
        let mutable right = size - 1
        let mutable upward = true
        while right >= 1 do
            if right = 6 then
                right <- 5
            for step = 0 to size - 1 do
                let y = if upward then size - 1 - step else step
                for colOffset = 0 to 1 do
                    let x = right - colOffset
                    if not isFunction.[y, x] then
                        let bit =
                            if bitIdx < totalBits then
                                ((int allCw.[bitIdx / 8] >>> (7 - (bitIdx % 8))) &&& 1) = 1
                            else
                                false
                        modules.[y, x] <- bit
                        bitIdx <- bitIdx + 1
            upward <- not upward
            right <- right - 2

        let mutable bestPenalty = Int32.MaxValue
        let mutable bestMatrix = modules
        for mask = 0 to 7 do
            let candidate = Array2D.copy modules
            for y = 0 to size - 1 do
                for x = 0 to size - 1 do
                    if not isFunction.[y, x] && applyMaskCondition mask x y then
                        candidate.[y, x] <- not candidate.[y, x]
            drawFormatBits candidate size mask
            let p = calculatePenalty candidate size
            if p < bestPenalty then
                bestPenalty <- p
                bestMatrix <- candidate

        bestMatrix

    /// <summary>QR コードを Avalonia WriteableBitmap（Bgra8888・依存パッケージ不要）へ直接描画する。</summary>
    let renderQrBitmap (text: string) (pixelsPerModule: int) (quietZone: int) : WriteableBitmap =
        let modules = encodeText text
        let size = modules.GetLength(0)
        let qz = max 4 quietZone
        let ppm = max 2 pixelsPerModule
        let imgSize = (size + qz * 2) * ppm
        let bmp = new WriteableBitmap(PixelSize(imgSize, imgSize), Vector(96.0, 96.0), PixelFormat.Bgra8888, AlphaFormat.Opaque)
        use fb = bmp.Lock()
        let rowStride = fb.RowBytes
        let rowBytes = Array.zeroCreate<byte> (imgSize * 4)

        for py = 0 to imgSize - 1 do
            let modY = (py / ppm) - qz
            let inRangeY = modY >= 0 && modY < size
            for px = 0 to imgSize - 1 do
                let modX = (px / ppm) - qz
                let isDark = inRangeY && modX >= 0 && modX < size && modules.[modY, modX]
                let offset = px * 4
                if isDark then
                    // BGRA (#FF1F1915)
                    rowBytes.[offset + 0] <- 0x15uy
                    rowBytes.[offset + 1] <- 0x19uy
                    rowBytes.[offset + 2] <- 0x1Fuy
                    rowBytes.[offset + 3] <- 0xFFuy
                else
                    // BGRA (#FFFFFFFF)
                    rowBytes.[offset + 0] <- 0xFFuy
                    rowBytes.[offset + 1] <- 0xFFuy
                    rowBytes.[offset + 2] <- 0xFFuy
                    rowBytes.[offset + 3] <- 0xFFuy
            let destPtr = IntPtr(fb.Address.ToInt64() + int64 py * int64 rowStride)
            Marshal.Copy(rowBytes, 0, destPtr, rowBytes.Length)
        bmp
