using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace WinSetupHelper.Services
{
    [DataContract]
    public sealed class CatalogEntry
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "category")] public string Category { get; set; }
        [DataMember(Name = "description")] public string Description { get; set; }
        [DataMember(Name = "recommended")] public bool Recommended { get; set; }

        /// <summary>Tùy chọn: mẫu tên trong "Programs and Features" (hỗ trợ dấu *).</summary>
        [DataMember(Name = "match", IsRequired = false, EmitDefaultValue = false)] public string[] Match { get; set; }

        /// <summary>Tùy chọn: tên file exe để nhận diện bản portable (vd. UniKeyNT.exe).</summary>
        [DataMember(Name = "exe", IsRequired = false, EmitDefaultValue = false)] public string[] Exe { get; set; }

        /// <summary>Tùy chọn: tên gói Store/MSIX (Appx), vd. Microsoft.WindowsTerminal.</summary>
        [DataMember(Name = "appx", IsRequired = false, EmitDefaultValue = false)] public string[] Appx { get; set; }
    }

    /// <summary>
    /// Đọc danh mục ứng dụng. Nếu cạnh file exe có "apps.json" thì dùng file đó
    /// (cho phép tự sửa danh mục mà không cần build lại), nếu không thì dùng bản nhúng sẵn.
    /// </summary>
    public static class Catalog
    {
        public static List<CatalogEntry> Load(Action<string> log)
        {
            var external = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "apps.json");
            if (File.Exists(external))
            {
                try
                {
                    var list = Parse(File.ReadAllText(external, Encoding.UTF8));
                    log?.Invoke("Đã nạp danh mục từ " + external);
                    return list;
                }
                catch (Exception ex)
                {
                    log?.Invoke("apps.json bên ngoài bị lỗi, dùng danh mục mặc định: " + ex.Message);
                }
            }

            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WinSetupHelper.apps.json"))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                return Parse(reader.ReadToEnd());
        }

        private static List<CatalogEntry> Parse(string json)
        {
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (List<CatalogEntry>)new DataContractJsonSerializer(typeof(List<CatalogEntry>)).ReadObject(ms)
                       ?? new List<CatalogEntry>();
        }
    }
}
