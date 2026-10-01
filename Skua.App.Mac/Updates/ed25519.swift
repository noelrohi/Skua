// Sparkle's EdDSA keys outside Sparkle, for release.sh: a private key is the base64 of its 32-byte Ed25519 seed, as generate_keys -x exports it.
//
//   swift ed25519.swift public-key < private-key        prints the public key, as SUPublicEDKey holds it
//   swift ed25519.swift verify <public-key> <file> <signature>
import CryptoKit
import Foundation

func fail(_ message: String) -> Never {
    FileHandle.standardError.write(Data("ed25519.swift: \(message)\n".utf8))
    exit(1)
}

func decode(_ text: String, _ what: String) -> Data {
    guard let data = Data(base64Encoded: text.trimmingCharacters(in: .whitespacesAndNewlines)) else { fail("\(what) isn't base64.") }
    return data
}

let arguments = CommandLine.arguments.dropFirst()
switch arguments.first {
case "public-key" where arguments.count == 1:
    let seed = decode(String(decoding: FileHandle.standardInput.readDataToEndOfFile(), as: UTF8.self), "the private key")
    guard seed.count == 32, let key = try? Curve25519.Signing.PrivateKey(rawRepresentation: seed) else {
        fail("the private key isn't a 32-byte Ed25519 seed, as Sparkle's generate_keys -x exports it.")
    }
    print(key.publicKey.rawRepresentation.base64EncodedString())
case "verify" where arguments.count == 4:
    let rest = Array(arguments.dropFirst())
    guard let key = try? Curve25519.Signing.PublicKey(rawRepresentation: decode(rest[0], "the public key")) else { fail("the public key isn't an Ed25519 key.") }
    guard let file = FileManager.default.contents(atPath: rest[1]) else { fail("can't read \(rest[1]).") }
    if !key.isValidSignature(decode(rest[2], "the signature"), for: file) { fail("\(rest[1]) isn't signed by the key \(rest[0]).") }
default:
    fail("usage: swift ed25519.swift public-key < private-key | verify <public-key> <file> <signature>")
}
