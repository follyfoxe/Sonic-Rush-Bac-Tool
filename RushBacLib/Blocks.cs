﻿using System.Diagnostics;
using System.Drawing;

namespace RushBacLib
{
    // The BAC format is little endian.
    // This code doesn't read the file in it's entirety, and isn't fully accurate.
    // More details here: https://www.romhacking.net/documents/669/
    // And some code was taken from here: https://github.com/NotKit/sonic-rush-tools/blob/master/bac.py

    public static class BlockUtility
    {
        public static byte[] ReadCompressed(BinaryReader reader, long offset)
        {
            // 1 byte: Compression? (0x00 = Uncompressed, 0x10 = LZSS)
            // 3 bytes: Size of data region
            reader.BaseStream.Seek(offset, SeekOrigin.Begin);

            uint header = reader.ReadUInt32();
            uint compression = header & 0xFF;
            uint uncompressedSize = header >> 8;

            if (compression == 0)
                return reader.ReadBytes((int)uncompressedSize);
            else if (compression == 0x10) // Compressed
                return new WiiLZ77(reader, offset).Uncompress();
            else
                throw new Exception("Invalid compression type: " + compression);
        }
    }

    public readonly struct Header
    {
        public static readonly uint BacMagic = BitConverter.ToUInt32(BacFile.Encoding.GetBytes("BAC\x0A"));

        public string MagicString => BacFile.Encoding.GetString(BitConverter.GetBytes(MagicNumber));
        public readonly uint MagicNumber;

        // Absolute offsets, relative to the beggining of the file.
        public readonly uint AnimationMappings;
        public readonly uint AnimationFrames;
        public readonly uint FrameAssembly;
        public readonly uint Palettes;
        public readonly uint ImageData;
        public readonly uint AnimationInfo;

        public Header(BinaryReader reader)
        {
            MagicNumber = reader.ReadUInt32();
            Trace.WriteLineIf(MagicNumber != BacMagic, "Stream is not a BAC file, magic does not match.");

            AnimationMappings = reader.ReadUInt32();
            AnimationFrames = reader.ReadUInt32();
            FrameAssembly = reader.ReadUInt32();
            Palettes = reader.ReadUInt32();
            ImageData = reader.ReadUInt32();
            AnimationInfo = reader.ReadUInt32();
        }
    }

    public readonly struct AnimationInfo // Block 1
    {
        public readonly uint BlockSize;
        public readonly ushort EntryCount;
        public readonly ushort EntrySize; // May be wrong
        public readonly byte[] Unknown;
        public readonly AnimationInfoEntry[] Entries;

        public AnimationInfo(BinaryReader reader)
        {
            BlockSize = reader.ReadUInt32();
            EntryCount = reader.ReadUInt16();
            EntrySize = reader.ReadUInt16();
            Unknown = reader.ReadBytes(20);
            Entries = new AnimationInfoEntry[EntryCount];
            for (int i = 0; i < EntryCount; i++)
                Entries[i] = new AnimationInfoEntry(reader);
        }
    }

    public readonly struct AnimationInfoEntry(BinaryReader reader)
    {
        public readonly byte[] Unknown = reader.ReadBytes(20);
    }

    public readonly struct AnimationMappings // Block 2, acts as an animation table of some sorts.
    {
        public readonly uint BlockSize;
        public readonly AnimationMapping[] Mappings;

        public AnimationMappings(BinaryReader reader)
        {
            BlockSize = reader.ReadUInt32();
            Mappings = new AnimationMapping[(BlockSize - 4) / 8];
            for (int i = 0; i < Mappings.Length; i++)
                Mappings[i] = new AnimationMapping(reader);
        }
    }

    public readonly struct AnimationMapping(BinaryReader reader)
    {
        public readonly uint FrameOffset = reader.ReadUInt32(); // Offset to Block 3's data, relative to Header.AnimationFrames.
        public readonly uint Unknown = reader.ReadUInt32(); // Might be flags of some kind?
    }

    public class AnimationFrames // Not 100% accurate
    {
        public readonly int RestingFrame;
        public readonly List<AnimationFrame> Frames;

        public AnimationFrames(BacFile file, BinaryReader reader)
        {
            Frames = [];

            AnimationFrame frame = null;
            Dictionary<long, int> frameOffsets = [];
            while (reader.BaseStream.Position < file.Header.FrameAssembly)
            {
                // ID 1 indicates a new frame and ID 4 acts as a terminator for the AnimationFrame.
                long start = reader.BaseStream.Position;
                ushort blockId = reader.ReadUInt16();
                ushort blockSize = reader.ReadUInt16();

                int size = blockSize - 4;
                switch (blockId)
                {
                    case 0: // Animation Info, not implemented
                        frame.Info = new AnimationFrame.FrameInfo(reader);
                        Trace.Assert(size == AnimationFrame.FrameInfo.Size, $"Size: {size}, Count: {size / AnimationFrame.FrameInfo.Size}");
                        break;
                    case 1: // Frame Assembly, usually the first frame block.
                        frame?.Build(file, reader);

                        frame = new AnimationFrame();
                        frameOffsets.Add(start - file.Header.AnimationFrames, Frames.Count);
                        Frames.Add(frame);

                        if (frame.FrameOffsets.Count > 0)
                            Trace.WriteLine("Frame Block 1 appeared more than once?");
                        size /= AnimationFrame.FrameOffset.Size;
                        for (int i = 0; i < size; i++)
                            frame.FrameOffsets.Add(new(reader));
                        break;
                    case 2: // Image Parts
                        if (frame.ImageParts.Count > 0)
                            Trace.WriteLine("Frame Block 2 appeared more than once?");
                        size /= AnimationFrame.ImagePart.Size;
                        for (int i = 0; i < size; i++)
                            frame.ImageParts.Add(new(reader));
                        break;
                    case 3: // Palette Parts
                        if (frame.PaletteParts.Count > 0)
                            Trace.WriteLine("Frame Block 3 appeared more than once?");
                        size /= AnimationFrame.PalettePart.Size;
                        for (int i = 0; i < size; i++)
                            frame.PaletteParts.Add(new(reader));
                        break;
                    case 4: // Resting Frame
                        frame?.Build(file, reader);
                        frame = null;

                        uint restingFrameOffset = reader.ReadUInt32();
                        if (!frameOffsets.TryGetValue(restingFrameOffset, out RestingFrame))
                            Trace.WriteLine("Couldn't extract resting frame! Perhaps it doesn't point to a valid location?");
                        break;
                    default:
                        Trace.WriteLine($"Unhandled Frame Block {blockId}.");
                        break;
                }

                reader.BaseStream.Seek(start + blockSize, SeekOrigin.Begin);
                if (blockId == 4)
                    break;
            }
        }
    }

    public class AnimationFrame // Custom helper thing, not exactly in the file specs
    {
        public readonly List<FrameOffset> FrameOffsets = []; // Offsets into FrameAssembly
        public readonly List<ImagePart> ImageParts = [];
        public readonly List<PalettePart> PaletteParts = [];

        public FrameInfo Info;
        public FrameAssembly FrameAssembly;
        public ImageData[] Images;
        public Palette Palette;

        // TODO: Refactor this...
        public void Build(BacFile file, BinaryReader reader)
        {
            long last = reader.BaseStream.Position;
            reader.BaseStream.Seek(file.Header.FrameAssembly + FrameOffsets[0].DataOffset, SeekOrigin.Begin);
            FrameAssembly = new FrameAssembly(reader);

            if (ImageParts.Count > 0)
            {
                Images = new ImageData[ImageParts.Count];
                for (int i = 0; i < Images.Length; i++)
                {
                    reader.BaseStream.Seek(file.Header.ImageData + ImageParts[i].DataOffset, SeekOrigin.Begin);
                    Images[i] = new ImageData(reader);
                }
            }
            else
                Trace.WriteLine("Warning: Frame doesn't have any Image Parts!");

            if (PaletteParts.Count > 0)
            {
                reader.BaseStream.Seek(file.Header.Palettes + PaletteParts[0].DataOffset, SeekOrigin.Begin);
                Palette = new Palette(reader);
            }
            else
                Trace.WriteLine("Warning: Frame doesn't have any Palettes!");
            reader.BaseStream.Seek(last, SeekOrigin.Begin);
        }

        // How to render properly: TopLeft = Position + (FrameX, FrameY)
        // https://osdl.sourceforge.net/main/documentation/misc/nintendo-DS/graphical-chain/OSDL-graphical-chain.html
        public Point GetTopLeft(Point centerPosition)
        {
            return centerPosition + new Size(FrameAssembly.FrameX, FrameAssembly.FrameY);
        }

        public Point GetBottomRight(Point centerPosition)
        {
            return centerPosition + new Size(FrameAssembly.FrameXRight, FrameAssembly.FrameYBottom);
        }

        public ImageResult GetImage(bool transparency = true, bool linear = false)
        {
            int width = FrameAssembly.FrameXRight - FrameAssembly.FrameX;
            int height = FrameAssembly.FrameYBottom - FrameAssembly.FrameY;

            ImageResult image = new(width, height);
            if (Images == null)
                return image;

            Color[] pal = Palette.GetColors(transparency);
            for (int i = 0; i < Images.Length; i++)
                FrameAssembly.DrawImage(image, pal, Images[i], i, linear);
            return image;
        }

        public readonly struct FrameInfo(BinaryReader reader)
        {
            public const int Size = 8;
            public readonly short FrameCount = reader.ReadInt16();
            public readonly short FrameIndex = reader.ReadInt16();
            public readonly uint Duration = reader.ReadUInt32();
        }

        public readonly struct FrameOffset(BinaryReader reader) // ID 01
        {
            public const int Size = 4;
            public readonly uint DataOffset = reader.ReadUInt32();
        }

        public readonly struct ImagePart(BinaryReader reader) // ID 02
        {
            public const int Size = 8;
            public readonly uint DataOffset = reader.ReadUInt32();
            public readonly uint DataSize = reader.ReadUInt32();
        }

        public readonly struct PalettePart(BinaryReader reader) // ID 03
        {
            public const int Size = 8;
            public readonly uint DataOffset = reader.ReadUInt32();
            public readonly uint ColorCount = reader.ReadUInt32();
        }
    }

    public readonly struct FrameAssembly
    {
        // Attributes from sprite.h of libnds
        // Attribute 0 consists of 8 bits of Y plus the following flags:
        public const int Attr0Square = 0 << 14;
        public const int Attr0Wide = 1 << 14;
        public const int Attr0Tall = 2 << 14;

        // Atribute 1 consists of 9 bits of X plus the following flags:
        public const int Attr1FlipX = 1 << 12;
        public const int Attr1FlipY = 1 << 13;
        public const int Attr1Size8 = 0 << 14;
        public const int Attr1Size16 = 1 << 14;
        public const int Attr1Size32 = 2 << 14;
        public const int Attr1Size64 = 3 << 14;

        public static readonly Size[] SizeSquare = [new(8, 8), new(16, 16), new(32, 32), new(64, 64)];
        public static readonly Size[] SizeWide = [new(16, 8), new(32, 8), new(32, 16), new(64, 32)];
        public static readonly Size[] SizeTall = [new(8, 16), new(8, 32), new(16, 32), new(32, 64)];

        public readonly ushort FramePartCount;
        public readonly ushort Unknown;
        public readonly short FrameX, FrameY, FrameXRight, FrameYBottom, HotSpotX, HotSpotY;
        public readonly ImagePartInfo[] PartInfos;

        public FrameAssembly(BinaryReader reader)
        {
            // When reading FramePartCount as a u4, ac_gmk_needle.bac has a value of 65537.
            // So it would make sense for it to actually be a u2. That leaves us with an extra unknown word.
            FramePartCount = reader.ReadUInt16();
            Unknown = reader.ReadUInt16();

            FrameX = reader.ReadInt16(); // Offset from center of the full image
            FrameY = reader.ReadInt16(); // Offset from center of the full image
            FrameXRight = reader.ReadInt16();
            FrameYBottom = reader.ReadInt16();
            HotSpotX = reader.ReadInt16(); // Always -FrameX ?
            HotSpotY = reader.ReadInt16(); // Always -FrameY ?

            PartInfos = new ImagePartInfo[FramePartCount];
            for (int i = 0; i < FramePartCount; i++)
                PartInfos[i] = new ImagePartInfo(reader);
        }

        public void DrawImage(ImageResult output, Color[] palette, ImageData imageData, int partIndex, bool linear)
        {
            ImagePartInfo info = PartInfos[partIndex];
            int partY = info.Attr0 & 0xFF;
            int partX = info.Attr1 & 0x1FF;
            int size = info.Attr1 >> 14;

            Size partSize;
            if ((info.Attr0 & Attr0Tall) != 0) // Sprite shape is NxM with N < M (Height > Width)
                partSize = SizeTall[size];
            else if ((info.Attr0 & Attr0Wide) != 0) // Sprite shape is NxM with N > M (Height < Width)
                partSize = SizeWide[size];
            else // Sprite shape is NxN (Height == Width)
                partSize = SizeSquare[size];

            ImageResult partImage = imageData.GetImage(palette, partSize.Width, partSize.Height, linear);
            output.DrawImage(partImage, partX, partY);
        }

        public readonly struct ImagePartInfo(BinaryReader reader)
        {
            public const int Size = 8;
            public readonly ushort Attr0 = reader.ReadUInt16();
            public readonly ushort Attr1 = reader.ReadUInt16();
            public readonly ushort Attr2 = reader.ReadUInt16();
            public readonly ushort Attr3 = reader.ReadUInt16();
        }
    }

    public readonly struct Palette(BinaryReader reader) // 4bpp rgb
    {
        public readonly byte[] Data = BlockUtility.ReadCompressed(reader, reader.BaseStream.Position);

        public Color[] GetColors(bool transparency = true)
        {
            Color[] colors = new Color[Data.Length / 2];
            for (int i = 0; i < Data.Length; i += 2)
            {
                int color = BitConverter.ToUInt16(Data, i);
                int r = color & 31;
                int g = (color & 31 << 5) >> 5;
                int b = (color & 31 << 10) >> 10;
                r = (int)(r * (255 / 31.0f));
                g = (int)(g * (255 / 31.0f));
                b = (int)(b * (255 / 31.0f));
                colors[i / 2] = Color.FromArgb(r, g, b);
            }
            if (transparency)
                colors[0] = Color.FromArgb(0, colors[0]); // Index 0 is transparent. Maybe use an index instead of a bool.
            return colors;
        }
    }

    public readonly struct ImageData(BinaryReader reader) // 4bpp
    {
        public readonly byte[] Data = BlockUtility.ReadCompressed(reader, reader.BaseStream.Position);

        public ImageResult GetImage(Color[] palette, int width, int height, bool linear)
        {
            ImageResult result = new(width, height);
            List<ImageResult> tiles = [];

            int bytesPerTile = 32;
            Size tileSize = new(8, 8);
            if (linear)
            {
                bytesPerTile = Data.Length;
                tileSize = new(width, height);
            }

            for (int tile = 0; tile < Data.Length; tile += bytesPerTile)
            {
                byte[] pixels = new byte[bytesPerTile * 2];
                for (int i = 0; i < pixels.Length; i += 2)
                {
                    byte b = Data[tile + i / 2];
                    pixels[i] = (byte)(b & 0x0F);
                    pixels[i + 1] = (byte)((b & 0xF0) >> 4);
                }

                ImageResult tileImage = new(tileSize.Width, tileSize.Height);
                for (int i = 0; i < pixels.Length; i++)
                    tileImage[i] = palette[pixels[i]];
                tiles.Add(tileImage);
            }

            int tilesPerRow = width / tileSize.Width;
            for (int i = 0; i < tiles.Count; i++)
                result.DrawImage(tiles[i], (i % tilesPerRow) * tileSize.Width, (i / tilesPerRow) * tileSize.Height);

            tiles.Clear();
            return result;
        }
    }
}