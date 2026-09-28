using System;
using System.Security.Cryptography;
using System.Text;

namespace Cruce
{
    /// <summary>Encryption and authentication keys derived from the shared secret.</summary>
    public sealed class Keys
    {
        public readonly byte[] Enc, Mac;

        public Keys(string secret)
        {
            var salt = Encoding.UTF8.GetBytes("Cruce/v1/pairing");
            using (var kdf = new Rfc2898DeriveBytes(secret ?? "", salt, 20000))
            {
                Enc = kdf.GetBytes(32);
                Mac = kdf.GetBytes(32);
            }
        }
    }

    /// <summary>
    /// AES-256-CTR + HMAC-SHA256 (encrypt-then-MAC) packet sealing.
    /// Layout: magic(2) sender(4) counter(8) ciphertext(n) tag(12).
    /// </summary>
    public sealed class PacketCrypto
    {
        public const int HeaderLen = 14, TagLen = 12, Overhead = HeaderLen + TagLen;
        const byte M0 = 0xC7, M1 = 0x2E;

        readonly ICryptoTransform ecb;
        readonly HMACSHA256 hmac;
        readonly object gate = new object();
        public readonly uint SenderId;
        long counter;

        public PacketCrypto(Keys keys)
        {
            var aes = new AesCryptoServiceProvider();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.Key = keys.Enc;
            ecb = aes.CreateEncryptor();
            hmac = new HMACSHA256(keys.Mac);
            var r = new byte[4];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(r);
            SenderId = BitConverter.ToUInt32(r, 0) | 1;
        }

        public byte[] Seal(byte[] plain, int len)
        {
            lock (gate)
            {
                var o = new byte[HeaderLen + len + TagLen];
                o[0] = M0; o[1] = M1;
                Put32(o, 2, SenderId);
                counter++;
                Put32(o, 6, (uint)counter);
                Put32(o, 10, (uint)(counter >> 32));
                Ctr(o, plain, 0, o, HeaderLen, len);
                var tag = hmac.ComputeHash(o, 0, HeaderLen + len);
                Buffer.BlockCopy(tag, 0, o, HeaderLen + len, TagLen);
                return o;
            }
        }

        /// <summary>Verifies and decrypts; returns null when the packet is not ours or was tampered with.</summary>
        public byte[] Open(byte[] buf, int len, out uint sender, out long ctr)
        {
            sender = 0; ctr = 0;
            if (len < Overhead + 1 || buf[0] != M0 || buf[1] != M1) return null;
            lock (gate)
            {
                var tag = hmac.ComputeHash(buf, 0, len - TagLen);
                int diff = 0;
                for (int i = 0; i < TagLen; i++) diff |= tag[i] ^ buf[len - TagLen + i];
                if (diff != 0) return null;
                sender = Get32(buf, 2);
                ctr = (long)Get32(buf, 6) | ((long)Get32(buf, 10) << 32);
                int n = len - Overhead;
                var p = new byte[n];
                Ctr(buf, buf, HeaderLen, p, 0, n);
                return p;
            }
        }

        // Keystream block i = AES(sender | counter | i); the 12-byte nonce is read from hdr[2..14].
        void Ctr(byte[] hdr, byte[] src, int so, byte[] dst, int d, int n)
        {
            if (n == 0) return;
            int blocks = (n + 15) / 16;
            var ctrBlocks = new byte[blocks * 16];
            for (int i = 0; i < blocks; i++)
            {
                Buffer.BlockCopy(hdr, 2, ctrBlocks, i * 16, 12);
                Put32(ctrBlocks, i * 16 + 12, (uint)i);
            }
            var ks = new byte[ctrBlocks.Length];
            ecb.TransformBlock(ctrBlocks, 0, ctrBlocks.Length, ks, 0);
            for (int j = 0; j < n; j++) dst[d + j] = (byte)(src[so + j] ^ ks[j]);
        }

        static void Put32(byte[] b, int o, uint v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
        static uint Get32(byte[] b, int o) { return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)); }
    }

    public sealed class WBuf
    {
        public byte[] B;
        public int P;
        public WBuf(int cap) { B = new byte[cap]; }

        void Ensure(int n) { if (P + n > B.Length) Array.Resize(ref B, Math.Max(B.Length * 2, P + n)); }
        public void U8(int v) { Ensure(1); B[P++] = (byte)v; }
        public void U16(int v) { Ensure(2); B[P++] = (byte)v; B[P++] = (byte)(v >> 8); }
        public void U32(uint v) { Ensure(4); B[P++] = (byte)v; B[P++] = (byte)(v >> 8); B[P++] = (byte)(v >> 16); B[P++] = (byte)(v >> 24); }
        public void I32(int v) { U32((uint)v); }
        public void I64(long v) { U32((uint)v); U32((uint)(v >> 32)); }
        public void Bytes(byte[] d, int o, int n) { Ensure(n); Buffer.BlockCopy(d, o, B, P, n); P += n; }
        public void Str(string s) { var b = Encoding.UTF8.GetBytes(s ?? ""); U16(b.Length); Bytes(b, 0, b.Length); }
        public byte[] ToArray() { var r = new byte[P]; Buffer.BlockCopy(B, 0, r, 0, P); return r; }
    }

    public sealed class RBuf
    {
        readonly byte[] b;
        int p;
        readonly int end;
        public RBuf(byte[] data, int off, int len) { b = data; p = off; end = off + len; }

        public int Left { get { return end - p; } }
        void Need(int n) { if (p + n > end) throw new FormatException("truncated"); }
        public int U8() { Need(1); return b[p++]; }
        public int U16() { Need(2); int v = b[p] | (b[p + 1] << 8); p += 2; return v; }
        public short I16() { return (short)U16(); }
        public uint U32() { Need(4); uint v = (uint)(b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24)); p += 4; return v; }
        public int I32() { return (int)U32(); }
        public long I64() { long lo = U32(); long hi = U32(); return lo | (hi << 32); }
        public byte[] Bytes(int n) { Need(n); var r = new byte[n]; Buffer.BlockCopy(b, p, r, 0, n); p += n; return r; }
        public string Str() { int n = U16(); Need(n); var s = Encoding.UTF8.GetString(b, p, n); p += n; return s; }
    }
}
