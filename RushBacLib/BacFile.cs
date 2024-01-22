using System;
using System.IO;

namespace RushBacLib
{
    public class BacFile
    {
        public Header Header;
        public AnimationInfo AnimationInfo;
        public AnimationMappings AnimationMappings;
        public AnimationFrame[] AnimationFrames;

        public BacFile(string path) : this(File.OpenRead(path)) { }

        public BacFile(Stream stream)
        {
            //Format is Little Indian
            BinaryReader reader = new BinaryReader(stream, System.Text.Encoding.UTF8);

            Header = new Header(reader);
            Console.WriteLine(Header.headerString);
            if (Header.pAnimationInfo != 0x1C) Console.WriteLine("Warning: Header points to Block1 not being at 0x1C! Ignoring...");
            else reader.BaseStream.Position = Header.pAnimationInfo;

            AnimationInfo = new AnimationInfo(reader);
            Console.WriteLine("Frame Count: " + AnimationInfo.frameCount);

            reader.BaseStream.Position = Header.pAnimationMappings;
            AnimationMappings = new AnimationMappings(this, reader);

            //Extract frames using AnimationMappings.
            AnimationFrames = new AnimationFrame[AnimationMappings.mappings.Length];
            for (int i = 0; i < AnimationFrames.Length; i++)
            {
                reader.BaseStream.Position = Header.pFrames + AnimationMappings.mappings[i].frameOffset - 4;
                AnimationFrames[i] = new AnimationFrame(this, reader);
            }
        }
    }
}