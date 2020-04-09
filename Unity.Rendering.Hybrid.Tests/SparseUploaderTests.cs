#if ENABLE_HYBRID_RENDERER_V2 && UNITY_2020_1_OR_NEWER && (HDRP_9_0_0_OR_NEWER || URP_9_0_0_OR_NEWER)

using NUnit.Framework;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;

namespace Unity.Rendering.Tests
{
    public class SparseUploaderTests
    {

        struct ExampleStruct
        {
            public int someData;
        }

        [Test]
        public void NoUploads()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            var buffer = new ComputeBuffer(1, UnsafeUtility.SizeOf<float>());
            var uploader = new SparseUploader(buffer);

            var tsu = uploader.Begin(1024, 1);
            uploader.EndAndCommit(tsu);

            uploader.Dispose();
            buffer.Dispose();
        }

        [Test]
        public void BasicUploads()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            var initialData = new ExampleStruct[1024];

            for (int i = 0; i < initialData.Length; ++i)
            {
                initialData[i] = new ExampleStruct { someData = i };
            }

            var buffer = new ComputeBuffer(initialData.Length, UnsafeUtility.SizeOf<ExampleStruct>());
            buffer.SetData(initialData);

            var uploader = new SparseUploader(buffer);

            {
                var tsu = uploader.Begin(UnsafeUtility.SizeOf<ExampleStruct>(), 1);
                tsu.AddUpload(new ExampleStruct { someData = 7 }, 4);
                uploader.EndAndCommit(tsu);
            }

            var resultingData = new ExampleStruct[initialData.Length];
            buffer.GetData(resultingData);

            Assert.AreEqual(0,resultingData[0].someData);
            Assert.AreEqual(7,resultingData[1].someData);
            Assert.AreEqual(2,resultingData[2].someData);

            {
                var tsu = uploader.Begin(UnsafeUtility.SizeOf<ExampleStruct>(), 1);
                tsu.AddUpload(new ExampleStruct { someData = 13 }, 8);
                uploader.EndAndCommit(tsu);
            }

            buffer.GetData(resultingData);

            Assert.AreEqual(0,resultingData[0].someData);
            Assert.AreEqual(7,resultingData[1].someData);
            Assert.AreEqual(13,resultingData[2].someData);
            Assert.AreEqual(3,resultingData[3].someData);

            uploader.Dispose();
            buffer.Dispose();
        }

        [Test]
        public void BigUploads()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            var initialData = new ExampleStruct[4 * 1024];

            for (int i = 0; i < initialData.Length; ++i)
            {
                initialData[i] = new ExampleStruct { someData = i };
            }

            var buffer = new ComputeBuffer(initialData.Length, UnsafeUtility.SizeOf<ExampleStruct>());
            buffer.SetData(initialData);

            var uploader = new SparseUploader(buffer);


            var newData = new ExampleStruct[312];
            for (int i = 0; i < newData.Length; ++i)
            {
                newData[i] = new ExampleStruct { someData = i + 3000 };
            }

            var newData2 = new ExampleStruct[316];
            for (int i = 0; i < newData2.Length; ++i)
            {
                newData2[i] = new ExampleStruct { someData = i + 4000 };
            }

            var tsu = uploader.Begin(UnsafeUtility.SizeOf<ExampleStruct>() * (newData.Length + newData2.Length), 2);
            unsafe
            {
                fixed (void* ptr = newData)
                {
                    tsu.AddUpload(ptr, newData.Length * 4, 512 * 4);
                }

                fixed (void* ptr2 = newData2)
                {
                    tsu.AddUpload(ptr2, newData2.Length * 4, 1136 * 4);
                }
            }

            uploader.EndAndCommit(tsu);

            var resultingData = new ExampleStruct[initialData.Length];
            buffer.GetData(resultingData);

            for (int i = 0; i < resultingData.Length; ++i)
            {
                if (i < 512)
                    Assert.AreEqual(i,resultingData[i].someData);
                else if(i < 824)
                    Assert.AreEqual(i - 512 + 3000,resultingData[i].someData);
                else if (i < 1136)
                    Assert.AreEqual(i,resultingData[i].someData);
                else if(i < 1452)
                    Assert.AreEqual(i - 1136 + 4000,resultingData[i].someData);
                else
                    Assert.AreEqual(i,resultingData[i].someData);
            }

            uploader.Dispose();
            buffer.Dispose();
        }

        struct UploadJob : IJobParallelFor
        {
            public ThreadedSparseUploader uploader;

            public void Execute(int index)
            {
                uploader.AddUpload(new ExampleStruct { someData = index}, index * 4);
            }
        }

        [Test]
        public void UploadFromJobs()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            var initialData = new ExampleStruct[4 * 1024];
            var stride = UnsafeUtility.SizeOf<ExampleStruct>();

            for (int i = 0; i < initialData.Length; ++i)
            {
                initialData[i] = new ExampleStruct { someData = 0 };
            }

            var buffer = new ComputeBuffer(initialData.Length, stride);
            buffer.SetData(initialData);

            var uploader = new SparseUploader(buffer);

            var job = new UploadJob();
            job.uploader = uploader.Begin(initialData.Length * stride, initialData.Length);
            job.Schedule(initialData.Length, 64).Complete();

            uploader.EndAndCommit(job.uploader);

            var resultingData = new ExampleStruct[initialData.Length];
            buffer.GetData(resultingData);

            for (int i = 0; i < resultingData.Length; ++i)
            {
                Assert.AreEqual(i, resultingData[i].someData);
            }

            uploader.Dispose();
            buffer.Dispose();
        }
    }
}

#endif // ENABLE_HYBRID_RENDERER_V2
