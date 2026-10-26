using System;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

public class DlcAppleScreenshotTests
{
    [Test] public void LandscapeImagePreservesCornersWithPortraitPadding() { CheckImage(80,40); }
    [Test] public void TallPortraitImagePreservesCornersWithSidePadding() { CheckImage(20,80); }
    static byte[] Source(int width,int height)
    {
        var texture=new Texture2D(width,height,TextureFormat.RGBA32,false,true);
        try {
            var pixels=new Color32[width*height];
            for(int y=0;y<height;y++) for(int x=0;x<width;x++)
                pixels[y*width+x]=y<height/2 ? (x<width/2 ? Color.red : Color.green) : (x<width/2 ? Color.blue : Color.white);
            texture.SetPixels32(pixels); texture.Apply();
            return ImageConversion.EncodeToPNG(texture);
        } finally { UnityEngine.Object.DestroyImmediate(texture); }
    }
    static void CheckImage(int width,int height)
    {
        var result=DlcAppleScreenshot.Prepare(Source(width,height));
        var output=new Texture2D(2,2);
        try {
            Assert.IsTrue(ImageConversion.LoadImage(output,result));
            Assert.AreEqual(1179,output.width); Assert.AreEqual(2556,output.height);
            double scale=Math.Min(1179.0/width,2556.0/height);
            int w=(int)Math.Round(width*scale), h=(int)Math.Round(height*scale);
            int left=(1179-w)/2,bottom=(2556-h)/2;
            Assert.AreEqual((Color32)Color.red,(Color32)output.GetPixel(left+2,bottom+2));
            Assert.AreEqual((Color32)Color.green,(Color32)output.GetPixel(left+w-3,bottom+2));
            Assert.AreEqual((Color32)Color.blue,(Color32)output.GetPixel(left+2,bottom+h-3));
            Assert.AreEqual((Color32)Color.white,(Color32)output.GetPixel(left+w-3,bottom+h-3));
            Assert.AreEqual((Color32)Color.black,(Color32)output.GetPixel(0,0));
        } finally { UnityEngine.Object.DestroyImmediate(output); }
    }
    [Test]
    public void ProductionExportUsesResizedBytesForUploadAndPreservesSource()
    {
        string root=Path.Combine(Path.GetTempPath(),"dlcpump-resize-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var fixture=new DlcStoreCatalogExportTests();
        try {
            var original=Source(80,40); string source=Path.Combine(root,"source.png"); File.WriteAllBytes(source,original);
            var manifest=DlcStoreCatalogExportTests.Manifest(); var options=DlcStoreCatalogExportTests.Options(manifest);
            options.products[0].appleReviewScreenshotPath=source;
            string folder=DlcStoreCatalogExport.Export(manifest,options,root);
            var bytes=File.ReadAllBytes(Path.Combine(folder,"apple/0001/review-screenshot.png"));
            CollectionAssert.AreEqual(original,File.ReadAllBytes(source));
            Assert.AreNotEqual(original.Length,bytes.Length);
            var image=new Texture2D(2,2);
            try { Assert.IsTrue(ImageConversion.LoadImage(image,bytes)); Assert.AreEqual(1179,image.width); Assert.AreEqual(2556,image.height); }
            finally { UnityEngine.Object.DestroyImmediate(image); }
            var reserve=JObject.Parse(File.ReadAllText(Path.Combine(folder,"apple/0001/05-screenshot-reserve.json")));
            Assert.AreEqual(bytes.LongLength,(long)reserve["data"]["attributes"]["fileSize"]);
            var commit=JObject.Parse(File.ReadAllText(Path.Combine(folder,"apple/0001/06-screenshot-commit.json")));
            using(var md5=MD5.Create()) Assert.AreEqual(BitConverter.ToString(md5.ComputeHash(bytes)).Replace("-","").ToLowerInvariant(),(string)commit["data"]["attributes"]["sourceFileChecksum"]);
        } finally { fixture.CleanupScreenshotFixture(); Directory.Delete(root,true); }
    }
}
