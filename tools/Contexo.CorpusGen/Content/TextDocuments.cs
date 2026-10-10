using System.Text;

namespace Contexo.CorpusGen.Content;

/// <summary>Seven plain files: two Big5 text files, Markdown, two HTML notices and two CSV exports.</summary>
internal static class TextDocuments
{
    public static void AddAll(CorpusBuilder corpus)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Big5(corpus, "舊版公告_停車證換發.txt",
            """
            曜陽科技 總務室 公告
            主旨：停車證換發通知

            一、本公司員工停車證自 11 月 1 日起換發新證。
            二、請持舊證至總務室辦理，每人限換一張。
            三、逾 11 月 30 日未換發者，舊證自 12 月 1 日起停用。

            總務室 敬啟
            """);
        Big5(corpus, "會議室使用規則.txt",
            """
            會議室使用規則

            1. 會議室採線上預約，單次最長可預約 2 小時。
            2. 使用後請恢復桌椅，並關閉投影機與空調。
            3. 如需取消預約，請於使用前一日下午 5 點前完成。
            4. 十樓大會議室需經部門主管核准後才能使用。
            """);
        Markdown(corpus);
        Html(corpus, "公告_停電通知.html", "停電通知",
            "<h1>停電通知</h1><p>配合大樓電力設備檢修，<strong>11 月 15 日（六）上午 8 點至下午 5 點</strong>全棟停電。</p>" +
            "<p>停電期間電梯停止服務，請各位同仁事先備份資料並關閉電腦。</p><ul><li>總機照常服務</li><li>機房由不斷電系統供電</li></ul>");
        Html(corpus, "公告_尾牙.html", "尾牙活動公告",
            "<h1>尾牙活動公告</h1><p>本年度尾牙訂於 2026 年 1 月 16 日（五）晚上六點，地點在台北晶華酒店三樓宴會廳。</p>" +
            "<p>請於 12 月 20 日前向總務室報名，並告知是否需要素食餐點。</p>");
        OrderExport(corpus);
        EmergencyContacts(corpus);
    }

    private static void Big5(CorpusBuilder corpus, string name, string text)
    {
        var big5 = Encoding.GetEncoding(950, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        corpus.AddFile(name, big5.GetBytes(text.ReplaceLineEndings("\r\n")));
    }

    private static void Markdown(CorpusBuilder corpus)
    {
        const string text = """
            # 新人手冊

            歡迎加入曜陽科技！這份手冊整理了你第一個月最常用到的資訊。

            ## 第一天

            - 報到時間：上午九點，地點為一樓櫃檯
            - 領取筆記型電腦與門禁卡
            - 資訊部同仁會協助你設定信箱

            ## 網路與 Wi-Fi

            員工請連線到 `Yaoyang-Staff`，使用公司帳號登入。訪客請連線到 `Yaoyang-Guest`，訪客 Wi-Fi 密碼每週一更換，請向櫃檯索取。

            ```
            vpn connect --profile yaoyang --user <你的帳號>
            ```

            ## 午餐與休息

            午休時間為中午十二點至一點，十二樓有員工餐廳，微波爐在每層樓的茶水間。
            """;
        corpus.AddFile("新人手冊.md", new UTF8Encoding(false).GetBytes(text.ReplaceLineEndings("\n")));
    }

    private static void Html(CorpusBuilder corpus, string name, string title, string body)
    {
        var html = $"<!DOCTYPE html>\n<html lang=\"zh-TW\">\n<head>\n<meta charset=\"utf-8\">\n<title>{title}</title>\n" +
                   "<style>body { font-family: sans-serif; }</style>\n</head>\n<body>\n" + body +
                   "\n<script>console.log('notice');</script>\n</body>\n</html>\n";
        corpus.AddFile(name, new UTF8Encoding(false).GetBytes(html));
    }

    private static void OrderExport(CorpusBuilder corpus)
    {
        var random = corpus.Random;
        var customers = new[] { "晨星科技", "雲林精工", "北辰貿易", "東昇工業", "南光食品", "西湖文創", "中興物流", "新豐建設" };
        var statuses = new[] { "已出貨", "已出貨", "已出貨", "處理中", "已取消" };
        var builder = new StringBuilder("訂單編號,客戶,金額,狀態\r\n");
        var shipped = 0;
        for (var i = 1; i <= 200; i++)
        {
            var status = statuses[random.Next(statuses.Length)];
            if (status == "已出貨")
            {
                shipped++;
            }

            builder.Append($"SO-2025-{i:D4},{customers[random.Next(customers.Length)]},{random.Next(5, 400) * 1000},{status}\r\n");
        }

        corpus.Expected.OrderCsvFile = "客戶訂單匯出.csv";
        corpus.Expected.OrderCsvRowCount = 200;
        corpus.Expected.OrderCsvShippedCount = shipped;
        // Excel exports UTF-8 CSV with a byte order mark.
        corpus.AddFile("客戶訂單匯出.csv", new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(builder.ToString())).ToArray());
    }

    private static void EmergencyContacts(CorpusBuilder corpus)
    {
        var rows = new List<string> { "單位,聯絡人,手機" };
        var departments = new[] { "總務室", "財務部", "業務部", "研發部", "人資部", "客服中心", "品保部", "倉儲課", "採購部" };
        for (var i = 0; i < 29; i++)
        {
            rows.Add($"{departments[i % departments.Length]}值班{i / departments.Length + 1},聯絡人{i + 1:D2},0900-000-{100 + i:D3}");
        }

        rows.Insert(1, "資訊部值班,王資訊,0900-123-456");
        corpus.AddFile("緊急聯絡人.csv", Encoding.UTF8.GetBytes(string.Join("\n", rows) + "\n"));
    }
}
