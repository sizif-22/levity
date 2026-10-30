using System;
using System.Collections.Generic;
using System.IO;
using Levity.Services;
using Xunit;

namespace Levity.Tests
{
    public class MobileImageTests
    {
        [Fact]
        public void AttachImagesToPrompt_FormatsSingleAndMultipleImages()
        {
            var single = MobileImages.AttachImagesToPrompt("Look at this", new[] { @"C:\uploads\img1.jpg" });
            Assert.Contains("Look at this", single);
            Assert.Contains("[Attached 1 image from mobile:]", single);
            Assert.Contains(@"- Image 1: C:\uploads\img1.jpg", single);

            var multi = MobileImages.AttachImagesToPrompt("Compare them", new[] { @"C:\uploads\img1.jpg", @"C:\uploads\img2.png" });
            Assert.Contains("[Attached 2 images from mobile:]", multi);
            Assert.Contains(@"- Image 1: C:\uploads\img1.jpg", multi);
            Assert.Contains(@"- Image 2: C:\uploads\img2.png", multi);

            var empty = MobileImages.AttachImagesToPrompt("No images", Array.Empty<string>());
            Assert.Equal("No images", empty);
        }

        [Fact]
        public void SaveIncomingImages_WritesBytesToDiskAndDetectsExtension()
        {
            byte[] fakeBytes = new byte[] { 1, 2, 3, 4, 5 };
            string b64 = Convert.ToBase64String(fakeBytes);

            var images = new[]
            {
                new MobileImages.IncomingImage("test.png", "image/png", b64),
                new MobileImages.IncomingImage("photo.jpeg", "image/jpeg", b64)
            };

            var paths = MobileImages.SaveIncomingImages(images);
            Assert.Equal(2, paths.Count);
            Assert.True(File.Exists(paths[0]));
            Assert.True(File.Exists(paths[1]));
            Assert.EndsWith(".png", paths[0]);
            Assert.EndsWith(".jpg", paths[1]);

            try
            {
                File.Delete(paths[0]);
                File.Delete(paths[1]);
            }
            catch { }
        }
    }
}
