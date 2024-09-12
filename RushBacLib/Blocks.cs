﻿using System.Diagnostics;
using System.Drawing;

namespace RushBacLib
{
    // The BAC format is little endian.
    // This code doesn't read the file in it's entirety, and isn't fully accurate.
    // More details here: https://www.romhacking.net/documents/669/

    public class Header
    {
        public static readonly uint BacMagic = BitConverter.ToUInt32(BacFile.Encoding.GetBytes("BAC\x0A"));

        public string MagicString => BacFile.Encoding.GetString(BitConverter.GetBytes(MagicNumber));
        public readonly uint MagicNumber;

        // Absolute offsets, from the beggining of the file.
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
            Trace.WriteLineIf(AnimationInfo != 0x1C, "Block 1 is not located at 0x1C.");
        }
    }

    public class AnimationInfo // Block 1
    {
        public readonly uint BlockSize;
        public readonly ushort EntryCount;
        public readonly ushort EntrySize;
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

    public class AnimationInfoEntry(BinaryReader reader)
    {
        public readonly byte[] Unknown = reader.ReadBytes(20);
    }

    public class AnimationMappings // Block 2, contains multiple AnimationFrames [SubBlock 1]
    {
        public readonly uint BlockSize;
        public readonly AnimationMapping[] Mappings;

        public AnimationMappings(BacFile file, BinaryReader reader)
        {
            BlockSize = reader.ReadUInt32();

            uint frameCount = file.AnimationInfo.EntryCount;
            if (BlockSize != frameCount * 8 + 4)
            {
                Trace.WriteLine("Warning: Block2's size is invalid. Trying to obtain correct Frame Count...");
                frameCount = (BlockSize - 4) / 8;
                Trace.WriteLine("Recalculated Frame Count: " + frameCount);
            }

            Mappings = new AnimationMapping[frameCount];
            for (int i = 0; i < Mappings.Length; i++)
                Mappings[i] = new AnimationMapping(reader);
        }
    }

    public class AnimationMapping(BinaryReader reader)
    {
        public readonly uint FrameOffset = reader.ReadUInt32(); // Offset to Block 3's data, relative to Header.AnimationFrames
        public readonly uint Unknown = reader.ReadUInt32();
    }

    // This is wrong. The total block size is given once and for Block 3 and AnimationMappings point to the frame data of Block3.
    public class AnimationFrame
    {
        public readonly uint totalBlockSize;
        public readonly uint restingFrame;
        public readonly List<Frame> frames;

        public AnimationFrame(BacFile file, BinaryReader reader)
        {
            totalBlockSize = reader.ReadUInt32();
            frames = new List<Frame>();

            Frame frame = null;
            ushort blockId, blockSize;

            while (reader.BaseStream.Position < file.Header.FrameAssembly)
            {
                blockId = reader.ReadUInt16();
                blockSize = reader.ReadUInt16();

                if (blockId == 4) //Resting/default frame?, as the block is not inside a "Frame", it can be used as a terminator
                {
                    frame?.Build(file, reader);
                    restingFrame = reader.ReadUInt32();
                    break;
                }

                switch (blockId)
                {
                    case 0: //Animation Info, not implemented //usually the last sub-sub-block.
                        reader.BaseStream.Position += blockSize - 4;
                        break;
                    case 1: //Frame Assembly, might have multiple parts //usually the first sub-sub-block.

                        frame?.Build(file, reader);
                        frame = new Frame();
                        frames.Add(frame);

                        int assemblyPartCount = (blockSize - 4) / 4;
                        frame.frameOffsets = new Frame.FrameOffset[assemblyPartCount];
                        for (int i = 0; i < assemblyPartCount; i++)
                            frame.frameOffsets[i] = new Frame.FrameOffset(reader);

                        break;
                    case 2: //Image Parts - goes into a "Frame"
                        int imagePartCount = (blockSize - 4) / 8;
                        frame.imageParts = new Frame.ImagePart[imagePartCount];
                        for (int i = 0; i < imagePartCount; i++)
                            frame.imageParts[i] = new Frame.ImagePart(reader);

                        break;
                    case 3: //Palette Parts - goes into a "Frame"
                        int palettePartCount = (blockSize - 4) / 8;
                        frame.paletteParts = new Frame.PalettePart[palettePartCount];
                        for (int i = 0; i < palettePartCount; i++)
                            frame.paletteParts[i] = new Frame.PalettePart(reader);

                        break;
                    default:
                        Trace.WriteLine($"Unknown Block {blockId}!");
                        reader.BaseStream.Position += blockSize - 4; //substracting 4 accounts for the blockId and blockSize.
                        break;
                }
            }
        }
    }

    public class Frame //Custom block/helper thing, not in the File Specs
    {
        public FrameOffset[] frameOffsets; //offsets into FrameAssembly
        public ImagePart[] imageParts;
        public PalettePart[] paletteParts;

        public FrameAssembly frameAssembly;
        public ImageData[] images;
        public Palette palette;

        public void Build(BacFile file, BinaryReader reader)
        {
            long last = reader.BaseStream.Position;
            reader.BaseStream.Position = file.Header.FrameAssembly + frameOffsets[0].offset;
            frameAssembly = new FrameAssembly(reader);

            if (imageParts != null)
            {
                images = new ImageData[imageParts.Length];
                for (int i = 0; i < images.Length; i++)
                {
                    reader.BaseStream.Position = file.Header.ImageData + imageParts[i].offset;
                    images[i] = new ImageData(reader);
                }
            }
            else
                Trace.WriteLine("Warning: Frame doesn't have any Image Parts!");

            if (paletteParts != null)
            {
                reader.BaseStream.Position = file.Header.Palettes + paletteParts[0].offset;
                palette = new Palette(reader);
            }
            else
                Trace.WriteLine("Warning: Frame doesn't have any Palettes!");

            reader.BaseStream.Position = last;
        }

        /*public Bitmap GetBitmap()
        {
            int width = frameAssembly.frameXRight - frameAssembly.frameX;
            int height = frameAssembly.frameYBottom - frameAssembly.frameY;

            Bitmap bitmap = new Bitmap(width, height);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                for (int i = 0; i < images?.Length; i++)
                    frameAssembly.DrawBitmap(g, palette, images[i], i);
            }
            return bitmap;
        }*/

        #region SubBlocks
        public readonly struct FrameOffset
        {
            public readonly uint offset;
            public FrameOffset(BinaryReader reader) => offset = reader.ReadUInt32();
        }

        public readonly struct ImagePart
        {
            public readonly uint offset;
            public readonly uint dataSize;

            public ImagePart(BinaryReader reader)
            {
                offset = reader.ReadUInt32();
                dataSize = reader.ReadUInt32();
            }
        }

        public readonly struct PalettePart
        {
            public readonly uint offset;
            public readonly uint colorCount;

            public PalettePart(BinaryReader reader)
            {
                offset = reader.ReadUInt32();
                colorCount = reader.ReadUInt32();
            }
        }
        #endregion
    }

    public class FrameAssembly
    {
        // Attributes from sprite.h of libnds
        // Attribute 0 consists of 8 bits of Y plus the following flags:
        public const int attr_0_square = (0 << 14);
        public const int attr_0_wide = (1 << 14);
        public const int attr_0_tall = (2 << 14);

        // Atribute 1 consists of 9 bits of X plus the following flags:
        public const int attr_1_flip_x = (1 << 12);
        public const int attr_1_flip_y = (1 << 13);
        public const int attr_1_size_8 = (0 << 14);
        public const int attr_1_size_16 = (1 << 14);
        public const int attr_1_size_32 = (2 << 14);
        public const int attr_1_size_64 = (3 << 14);

        public static readonly Point[] size_square = { new Point(8, 8), new Point(16, 16), new Point(32, 32), new Point(64, 64) };
        public static readonly Point[] size_wide = { new Point(16, 8), new Point(32, 8), new Point(32, 16), new Point(64, 32) };
        public static readonly Point[] size_tall = { new Point(8, 16), new Point(8, 32), new Point(16, 32), new Point(32, 64) };

        public readonly uint framePartCount;
        public readonly short frameX, frameY, frameXRight, frameYBottom, hotSpotX, hotSpotY;

        public ImagePartInfo[] partInfos;

        public FrameAssembly(BinaryReader reader)
        {
            framePartCount = reader.ReadUInt32();
            frameX = reader.ReadInt16();
            frameY = reader.ReadInt16();
            frameXRight = reader.ReadInt16();
            frameYBottom = reader.ReadInt16();
            hotSpotX = reader.ReadInt16();
            hotSpotY = reader.ReadInt16();

            partInfos = new ImagePartInfo[framePartCount];
            for (int i = 0; i < framePartCount; i++)
                partInfos[i] = new ImagePartInfo(reader);
        }

        /*public void DrawBitmap(Graphics g, Palette palette, ImageData image, int partIndex)
        {
            ImagePartInfo info = partInfos[partIndex];

            int partY = info.attr0 & 0xFF;
            int partX = info.attr1 & 0x1FF;
            int size = info.attr1 >> 14;

            Point partSize;
            if ((info.attr0 & attr_0_tall) != 0) //Sprite shape is NxM with N < M (Height > Width)
                partSize = size_tall[size];
            else if ((info.attr0 & attr_0_wide) != 0) //Sprite shape is NxM with N > M (Height < Width)
                partSize = size_wide[size];
            else //Sprite shape is NxN (Height == Width)
                partSize = size_square[size];

            using (Bitmap bitmap = image.GetBitmap(palette, partSize.X, partSize.Y))
                g.DrawImage(bitmap, partX, partY);
        }*/

        public readonly struct ImagePartInfo
        {
            public readonly ushort attr0;
            public readonly ushort attr1;
            public readonly ushort attr2;
            public readonly ushort attr3;

            public ImagePartInfo(BinaryReader reader)
            {
                attr0 = reader.ReadUInt16();
                attr1 = reader.ReadUInt16();
                attr2 = reader.ReadUInt16();
                attr3 = reader.ReadUInt16();
            }
        }
    }

    public static class BlockUtility
    {
        public static byte[] ReadCompressed(BinaryReader reader)
        {
            //1 byte: Compression? (0x00=Uncompressed, 0x10=LZSS)
            //3 bytes: Size of data region
            uint header = reader.ReadUInt32();
            uint compression = header & 0xFF;
            uint uncompressedSize = header >> 8;

            if (compression == 0)
                return reader.ReadBytes((int)uncompressedSize);
            else if (compression == 0x10) //Compressed
                throw new Exception("Compression is not supported!");
            else
                throw new Exception("Invalid compression type: " + compression);
        }
    }

    public class Palette //4bpp rgb
    {
        public readonly byte[] data;
        public readonly Color[] entries;

        public Palette(BinaryReader reader)
        {
            data = BlockUtility.ReadCompressed(reader);

            entries = new Color[data.Length / 2];
            for (int i = 0; i < data.Length; i += 2)
            {
                int color = BitConverter.ToUInt16(data, i);
                int r = color & 31;
                int g = (color & 31 << 5) >> 5;
                int b = (color & 31 << 10) >> 10;
                r = (int)(r * (255 / 31.0f));
                g = (int)(g * (255 / 31.0f));
                b = (int)(b * (255 / 31.0f));
                entries[i / 2] = Color.FromArgb(r, g, b);
            }
        }

        /*public void SetColorPalette(Bitmap bitmap)
        {
            ColorPalette pal = bitmap.Palette; //returns a copy
            entries.CopyTo(pal.Entries, 0);
            bitmap.Palette = pal;
        }*/
    }

    public class ImageData //4bpp
    {
        public readonly byte[] data;

        public ImageData(BinaryReader reader)
        {
            data = BlockUtility.ReadCompressed(reader);
        }

        /*public Bitmap GetBitmap(Palette palette, int width, int height)
        {
            Bitmap bitmap = new Bitmap(width, height);

            List<Bitmap> tiles = new List<Bitmap>();
            int bytesPerTile = 32;

            for (int tile = 0; tile < data.Length; tile += bytesPerTile)
            {
                byte[] pixels = new byte[bytesPerTile * 2];
                for (int i = 0; i < pixels.Length; i += 2)
                {
                    byte b = data[tile + i / 2];

                    pixels[i] = (byte)(b & 0x0F);
                    pixels[i + 1] = (byte)((b & 0xF0) >> 4);
                }

                Bitmap tileb = new Bitmap(8, 8, PixelFormat.Format8bppIndexed);
                palette.SetColorPalette(tileb);

                BitmapData bdata = tileb.LockBits(new Rectangle(0, 0, 8, 8), ImageLockMode.WriteOnly, tileb.PixelFormat);
                System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bdata.Scan0, bdata.Stride * bdata.Height);
                tileb.UnlockBits(bdata);

                tiles.Add(tileb);
            }

            using (Graphics g = Graphics.FromImage(bitmap))
            {
                int tilesPerRow = width / 8;
                for (int i = 0; i < tiles.Count; i++)
                    g.DrawImage(tiles[i], (i % tilesPerRow) * 8, (i / tilesPerRow) * 8);
            }

            foreach (Bitmap b in tiles) b.Dispose();
            tiles.Clear();

            return bitmap;
        }*/
    }
}