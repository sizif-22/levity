using System;
using System.Collections.Generic;
using System.IO;
using Levity.Services;
using Xunit;

namespace Levity.Tests
{
    public class NotebookStoreTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly NotebookStore _store;

        public NotebookStoreTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "LevityTestNotebook_" + Guid.NewGuid().ToString("N"));
            _store = new NotebookStore(_tempDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, true);
                }
            }
            catch { }
        }

        [Fact]
        public void EmptyStore_ReturnsEmptyList()
        {
            var notes = _store.GetNotes();
            Assert.NotNull(notes);
            Assert.Empty(notes);
        }

        [Fact]
        public void SaveNote_PersistsAndLoads()
        {
            var note = new NotebookStore.NoteItem(
                Id: "note-1",
                Title: "My Personal Note",
                Content: "# Todo\n- Test notebook\n- Verify images",
                Images: null,
                CreatedAt: 1000,
                UpdatedAt: 2000,
                Pinned: false
            );

            var saved = _store.SaveNote(note);
            Assert.Single(saved);
            Assert.Equal("My Personal Note", saved[0].Title);
            Assert.Equal("note-1", saved[0].Id);

            var loaded = _store.GetNotes();
            Assert.Single(loaded);
            Assert.Equal("My Personal Note", loaded[0].Title);
            Assert.Contains("# Todo", loaded[0].Content);
        }

        [Fact]
        public void SaveNote_WithImageAttachment_SavesImageFile()
        {
            byte[] dummyBytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            string base64 = Convert.ToBase64String(dummyBytes);

            var img = new NotebookStore.NoteImage(
                Id: "img-1",
                Name: "screenshot.png",
                MimeType: "image/png",
                Url: "",
                Data: base64
            );

            var note = new NotebookStore.NoteItem(
                Id: "note-with-img",
                Title: "Note with image",
                Content: "Here is a screenshot",
                Images: new List<NotebookStore.NoteImage> { img },
                CreatedAt: 1000,
                UpdatedAt: 2000
            );

            var result = _store.SaveNote(note);
            Assert.Single(result);
            Assert.NotNull(result[0].Images);
            Assert.Single(result[0].Images!);

            var savedImg = result[0].Images![0];
            Assert.Equal("img-1", savedImg.Id);
            Assert.Equal("screenshot.png", savedImg.Name);
            Assert.Contains("screenshot.png", savedImg.Url);

            // Verify image was saved on disk
            string filename = $"{savedImg.Id}_{savedImg.Name}";
            byte[]? readBytes = _store.GetImageBytes(filename);
            Assert.NotNull(readBytes);
            Assert.Equal(dummyBytes, readBytes);
        }

        [Fact]
        public void DeleteNote_RemovesNoteAndAssociatedImages()
        {
            byte[] dummyBytes = new byte[] { 9, 8, 7 };
            string base64 = Convert.ToBase64String(dummyBytes);

            var note = new NotebookStore.NoteItem(
                Id: "del-note",
                Title: "To delete",
                Content: "Content",
                Images: new List<NotebookStore.NoteImage>
                {
                    new("del-img", "test.jpg", "image/jpeg", "", base64)
                },
                CreatedAt: 1000,
                UpdatedAt: 2000
            );

            _store.SaveNote(note);
            Assert.Single(_store.GetNotes());

            var deleted = _store.DeleteNote("del-note");
            Assert.Empty(deleted);
            Assert.Empty(_store.GetNotes());

            // Image file should be deleted
            byte[]? readBytes = _store.GetImageBytes("del-img_test.jpg");
            Assert.Null(readBytes);
        }
    }
}
