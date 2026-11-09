// ASTC encoder using macOS ImageIO (org.khronos.astc).
// Batch protocol on stdin/stdout, repeated until EOF:
//   in : u32 width, u32 height, u32 blockSize (4 or 8), f32 quality, then width*height*4 RGBA8 bytes
//   out: u32 n, then n bytes of raw ASTC blocks (the 16-byte .astc header stripped), n = 0 on failure
import Foundation
import ImageIO
import CoreGraphics
import UniformTypeIdentifiers

func readExact(_ n: Int) -> Data? {
    var d = Data(); d.reserveCapacity(n)
    while d.count < n {
        let chunk = FileHandle.standardInput.readData(ofLength: n - d.count)
        if chunk.isEmpty { return nil }
        d.append(chunk)
    }
    return d
}
func u32(_ d: Data, _ o: Int) -> UInt32 { d.subdata(in: o..<o+4).withUnsafeBytes { $0.load(as: UInt32.self) } }
func writeU32(_ v: UInt32) { var x = v; FileHandle.standardOutput.write(Data(bytes: &x, count: 4)) }

let srgb = CGColorSpace(name: CGColorSpace.sRGB)!
while let hdr = readExact(16) {
    let w = Int(u32(hdr, 0)), h = Int(u32(hdr, 4)), bs = Int(u32(hdr, 8))
    let q = hdr.subdata(in: 12..<16).withUnsafeBytes { $0.load(as: Float.self) }
    guard let px = readExact(w * h * 4) else { break }
    var out = Data()
    let provider = CGDataProvider(data: px as CFData)!
    let info = CGBitmapInfo(rawValue: CGImageAlphaInfo.last.rawValue)
    if let img = CGImage(width: w, height: h, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: w * 4,
                         space: srgb, bitmapInfo: info, provider: provider, decode: nil,
                         shouldInterpolate: false, intent: .defaultIntent) {
        let md = NSMutableData()
        if let dst = CGImageDestinationCreateWithData(md, "org.khronos.astc" as CFString, 1, nil) {
            var props: [CFString: Any] = [kCGImageDestinationLossyCompressionQuality: q]
            props[kCGImagePropertyASTCBlockSize] = bs == 8 ? kCGImagePropertyASTCBlockSize8x8 : kCGImagePropertyASTCBlockSize4x4
            CGImageDestinationAddImage(dst, img, props as CFDictionary)
            if CGImageDestinationFinalize(dst), md.length > 16 { out = (md as Data).subdata(in: 16..<md.length) }
        }
    }
    writeU32(UInt32(out.count)); FileHandle.standardOutput.write(out)
}
