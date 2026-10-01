using System.Buffers.Binary;
using System.Security.Cryptography;

namespace CadMcp.Core;

public sealed record ReferenceImage(string Path, int Width, int Height, string MimeType, string Sha256, int ExifOrientation, byte[] Bytes)
{
    public object Metadata => new { path=Path, width=Width, height=Height, mime_type=MimeType, sha256=Sha256,
        exif_orientation=ExifOrientation, pixel_origin="top_left_display_orientation", bytes=Bytes.Length };
    public static ReferenceImage Read(string path)
    {
        path=System.IO.Path.GetFullPath(path); using var stream=File.OpenRead(path);
        if(stream.Length is <12 or >7*1024*1024) throw new CadFault("INVALID_REFERENCE_IMAGE","Image must be 12 bytes..7 MiB");
        var bytes=new byte[(int)stream.Length]; stream.ReadExactly(bytes);
        int w=0,h=0,orientation=1; string mime;
        if(bytes.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}) && bytes.Length>=24)
        { w=BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16,4)); h=BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20,4)); mime="image/png"; }
        else if(bytes[0]==255 && bytes[1]==216)
        {
            mime="image/jpeg"; int offset=2;
            while(offset+4<=bytes.Length)
            {
                if(bytes[offset++]!=255) break;
                while(offset<bytes.Length && bytes[offset]==255) offset++;
                if(offset>=bytes.Length)break; int marker=bytes[offset++];
                if(marker is 0xD9 or 0xDA)break;
                if(marker is 0x01 or >=0xD0 and <=0xD7)continue;
                if(offset+2>bytes.Length)break; int length=BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset,2));
                if(length<2 || offset+length>bytes.Length)break;
                if(marker==0xE1 && length>=16 && bytes.AsSpan(offset+2,6).SequenceEqual("Exif\0\0"u8)) orientation=Orientation(bytes,offset+8,length-8);
                if(marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
                { if(length>=7) { h=BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset+3,2)); w=BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset+5,2)); } }
                offset+=length;
            }
        }
        else if(bytes.AsSpan(0,6).SequenceEqual("GIF87a"u8)||bytes.AsSpan(0,6).SequenceEqual("GIF89a"u8))
        { w=BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6,2)); h=BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8,2)); mime="image/gif"; }
        else if(bytes.Length>=30 && bytes.AsSpan(0,4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8,4).SequenceEqual("WEBP"u8))
        {
            mime="image/webp";
            if(bytes.AsSpan(12,4).SequenceEqual("VP8X"u8)) { w=1+bytes[24]+(bytes[25]<<8)+(bytes[26]<<16); h=1+bytes[27]+(bytes[28]<<8)+(bytes[29]<<16); }
            else if(bytes.AsSpan(12,4).SequenceEqual("VP8L"u8)&&bytes[20]==0x2f) { w=1+bytes[21]+((bytes[22]&63)<<8); h=1+(bytes[22]>>6)+(bytes[23]<<2)+((bytes[24]&15)<<10); }
            else if(bytes.AsSpan(12,4).SequenceEqual("VP8 "u8)&&bytes.AsSpan(23,3).SequenceEqual(new byte[]{0x9d,1,0x2a}))
            { w=BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26,2))&0x3fff; h=BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28,2))&0x3fff; }
        }
        else throw new CadFault("INVALID_REFERENCE_IMAGE","Use PNG/JPEG/WebP/GIF");
        if(w is <1 or >100000||h is <1 or >100000) throw new CadFault("INVALID_REFERENCE_IMAGE","Image dimensions are unavailable or invalid");
        if(orientation is >=5 and <=8) (w,h)=(h,w);
        return new(path,w,h,mime,Convert.ToHexString(SHA256.HashData(bytes)),orientation,bytes);
    }
    private static int Orientation(byte[] data,int start,int length)
    {
        if(length<8)return 1;
        bool little=data[start]==(byte)'I' && data[start+1]==(byte)'I';
        if(!little && !(data[start]==(byte)'M'&&data[start+1]==(byte)'M'))return 1;
        ushort U16(int p)=>little?BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p,2)):BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p,2));
        uint U32(int p)=>little?BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p,4)):BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p,4));
        uint offset=U32(start+4); if(offset>length-2)return 1; int p=start+(int)offset, count=Math.Min(64,(int)U16(p));
        for(int i=0;i<count;i++) { int entry=p+2+i*12; if(entry+12>start+length)break;
            if(U16(entry)==0x112&&U16(entry+2)==3&&U32(entry+4)==1) { int result=U16(entry+8); return result is >=1 and <=8?result:1; } }
        return 1;
    }
}
