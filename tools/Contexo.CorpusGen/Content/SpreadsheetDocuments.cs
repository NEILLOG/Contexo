using Contexo.CorpusGen.Office;

namespace Contexo.CorpusGen.Content;

/// <summary>Ten Excel workbooks: a quotation form, large tables for SQL, two tables on one sheet, two-level headers, formulas.</summary>
internal static class SpreadsheetDocuments
{
    private static readonly string[] SalesCustomers =
    [
        "晨星科技", "雲林精工", "北辰貿易", "東昇工業", "南光食品", "西湖文創", "中興物流", "新豐建設", "金鼎餐飲", "永盛紡織",
        "鴻圖印刷", "嘉澤生技", "華揚貿易", "立誠機械", "博達顧問", "寰宇旅行社", "明德教育", "崇光百貨", "泰安醫材", "志遠能源",
    ];

    private static readonly (string Name, int Price)[] SalesProducts =
    [
        ("天眼 X2 攝影機", 9800), ("天眼 X1 攝影機", 6200), ("錄影主機 16 路", 32000), ("錄影主機 32 路", 68000),
        ("門禁控制器", 14500), ("感應讀卡機", 3800), ("PoE 交換器", 15800), ("年度維護合約", 45000),
    ];

    private static readonly string[] Surnames = ["陳", "林", "黃", "張", "李", "王", "吳", "劉", "蔡", "楊", "許", "鄭", "謝", "郭", "洪", "邱"];
    private static readonly string[] GivenNames = ["雅婷", "家豪", "怡君", "志明", "淑芬", "建宏", "佳穎", "俊傑", "靜怡", "宗翰", "欣怡", "冠宇", "美玲", "柏翰", "詩涵", "承恩"];
    private static readonly string[] Departments = ["業務部", "研發部", "資訊部", "財務部", "總務室"];

    public static void AddAll(CorpusBuilder corpus)
    {
        Quotation(corpus);
        CustomerList(corpus);
        SalesDetail(corpus);
        Inventory(corpus);
        TwoTablesOnOneSheet(corpus);
        TwoLevelHeader(corpus);
        TravelExpense(corpus);
        ProjectBudget(corpus);
        StaffDirectory(corpus);
        Attendance(corpus);
    }

    private static void Quotation(CorpusBuilder corpus)
    {
        var workbook = new XlsxBuilder();
        var sheet = workbook.AddSheet("報價單");
        sheet.Set("A1", "曜陽科技股份有限公司 報價單", CellStyle.Bold).Merge("A1:E1");
        sheet.Set("A3", "客戶名稱", CellStyle.Bold).Set("B3", "台中市政府資訊局");
        sheet.Set("A4", "專案名稱", CellStyle.Bold).Set("B4", "台中案 智慧監控系統");
        sheet.Set("A5", "報價日期", CellStyle.Bold).Set("B5", new DateTime(2025, 8, 15), CellStyle.Date);
        sheet.Set("A6", "有效期限", CellStyle.Bold).Set("B6", "30 天");
        sheet.SetRow(7, 0, CellStyle.BoldFill, "項次", "品名", "數量", "單價", "金額");
        sheet.Set(8, 0, 1).Set(8, 1, "監視系統（含 32 路錄影主機與 16 支攝影機）").Set(8, 2, 1).Set(8, 3, 1285000, CellStyle.Thousands).Set(8, 4, 1285000, CellStyle.Thousands);
        sheet.Set(9, 0, 2).Set(9, 1, "門禁管制系統").Set(9, 2, 1).Set(9, 3, 468000, CellStyle.Thousands).Set(9, 4, 468000, CellStyle.Thousands);
        sheet.Set(10, 0, 3).Set(10, 1, "機房不斷電設備").Set(10, 2, 2).Set(10, 3, 86000, CellStyle.Thousands).Set(10, 4, 172000, CellStyle.Thousands);
        sheet.Set(11, 0, 4).Set(11, 1, "安裝與教育訓練").Set(11, 2, 1).Set(11, 3, 120000, CellStyle.Thousands).Set(11, 4, 120000, CellStyle.Thousands);
        sheet.Set(13, 3, "合計（未稅）", CellStyle.Bold).Set(13, 4, 2045000, CellStyle.Thousands);
        sheet.Set(14, 3, "營業稅 5%").Set(14, 4, 102250, CellStyle.Thousands);
        sheet.Set(15, 3, "總計", CellStyle.Bold).Set(15, 4, 2147250, CellStyle.Thousands);
        corpus.AddOffice("2025_台中案_報價單.xlsx", workbook.Build());
    }

    private static void CustomerList(CorpusBuilder corpus)
    {
        var prefixes = new[] { "宏", "益", "瑞", "昇", "冠", "鼎", "信", "富", "康", "祥", "盛", "華", "凱", "傑", "晟" };
        var middles = new[] { "達", "發", "豐", "佳", "展", "通", "亞", "海", "山", "泰" };
        var kinds = new[] { "科技", "工業", "貿易", "實業", "電子", "食品", "營造", "物流" };
        var regions = new[] { "北部", "北部", "中部", "南部", "東部" };
        var levels = new[] { "A", "B", "B", "C", "C", "C" };

        var random = corpus.Random;
        var workbook = new XlsxBuilder();
        var sheet = workbook.AddSheet("客戶清單");
        sheet.SetRow(0, 0, CellStyle.Bold, "客戶編號", "公司名稱", "聯絡人", "電話", "地區", "等級");
        var north = 0;
        for (var i = 0; i < 500; i++)
        {
            var region = regions[random.Next(regions.Length)];
            if (region == "北部")
            {
                north++;
            }

            var name = prefixes[random.Next(prefixes.Length)] + middles[random.Next(middles.Length)] + kinds[random.Next(kinds.Length)] + "有限公司";
            var contact = Surnames[random.Next(Surnames.Length)] + GivenNames[random.Next(GivenNames.Length)];
            var phone = $"0{random.Next(2, 9)}-{random.Next(2000, 9999)}-{random.Next(1000, 9999)}";
            sheet.SetRow(i + 1, 0, CellStyle.Plain, $"C{i + 1:D4}", name, contact, phone, region, levels[random.Next(levels.Length)]);
        }

        corpus.Expected.CustomerListFile = "客戶清單.xlsx";
        corpus.Expected.CustomerListRowCount = 500;
        corpus.Expected.CustomerListNorthCount = north;
        corpus.AddOffice("客戶清單.xlsx", workbook.Build());
    }

    private static void SalesDetail(CorpusBuilder corpus)
    {
        var random = corpus.Random;
        var workbook = new XlsxBuilder();
        var sheet = workbook.AddSheet("銷售明細");
        sheet.SetRow(0, 0, CellStyle.Bold, "日期", "客戶", "產品", "數量", "單價", "金額");
        var byCustomer = new Dictionary<string, long>();
        long total = 0;
        var start = new DateTime(2025, 1, 1);
        for (var i = 0; i < 3000; i++)
        {
            var date = start.AddDays(random.Next(0, 365));
            var customer = SalesCustomers[random.Next(SalesCustomers.Length)];
            var (product, price) = SalesProducts[random.Next(SalesProducts.Length)];
            var quantity = random.Next(1, 21);
            var amount = (long)quantity * price;
            sheet.Set(i + 1, 0, date, CellStyle.Date);
            sheet.Set(i + 1, 1, customer).Set(i + 1, 2, product).Set(i + 1, 3, quantity).Set(i + 1, 4, price).Set(i + 1, 5, amount);
            total += amount;
            byCustomer[customer] = byCustomer.GetValueOrDefault(customer) + amount;
        }

        var ranked = byCustomer.OrderByDescending(p => p.Value).ToList();
        if (ranked[0].Value == ranked[1].Value)
        {
            throw new InvalidOperationException("The sales data has two top customers; change the seed.");
        }

        var expected = corpus.Expected;
        expected.SalesFile = "銷售明細.xlsx";
        expected.SalesRowCount = 3000;
        expected.SalesTotalAmount = total;
        expected.SalesTopCustomer = ranked[0].Key;
        expected.SalesTopCustomerAmount = ranked[0].Value;
        expected.SalesAmountByCustomer = byCustomer.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value);
        corpus.AddOffice("銷售明細.xlsx", workbook.Build());
    }

    private static void Inventory(CorpusBuilder corpus)
    {
        var random = corpus.Random;
        var names = new[] { "電源供應器", "網路線 Cat6", "光纖跳線", "機櫃風扇", "標籤紙", "固定螺絲組", "電池模組", "訊號放大器" };
        var workbook = new XlsxBuilder();
        var sheet = workbook.AddSheet("庫存");
        sheet.SetRow(0, 0, CellStyle.Bold, "料號", "品名", "庫存量", "倉位", "安全庫存");
        for (var i = 0; i < 25; i++)
        {
            if (i == 11)
            {
                sheet.SetRow(i + 1, 0, CellStyle.Plain, "XYZ-789", "紅外線感應器", 47, "B-03", 20);
                continue;
            }

            sheet.SetRow(i + 1, 0, CellStyle.Plain, $"PRT-{100 + i}", names[i % names.Length], random.Next(5, 300), $"{(char)('A' + (i % 4))}-{(i % 9) + 1:D2}", random.Next(10, 60));
        }

        corpus.AddOffice("庫存清單.xlsx", workbook.Build());
    }

    private static void TwoTablesOnOneSheet(CorpusBuilder corpus)
    {
        var workbook = new XlsxBuilder();
        var sheet = workbook.AddSheet("總覽");
        sheet.Set("A1", "部門人數", CellStyle.Bold);
        sheet.SetRow(1, 0, CellStyle.BoldFill, "部門", "人數");
        sheet.SetRow(2, 0, CellStyle.Plain, "業務部", 12);
        sheet.SetRow(3, 0, CellStyle.Plain, "研發部", 18);
        sheet.SetRow(4, 0, CellStyle.Plain, "資訊部", 6);
        sheet.SetRow(5, 0, CellStyle.Plain, "財務部", 5);
        sheet.SetRow(6, 0, CellStyle.Plain, "總務室", 4);

        sheet.Set("A10", "設備借用登記", CellStyle.Bold);
        sheet.SetRow(10, 0, CellStyle.BoldFill, "設備", "借用人", "借用日期");
        var items = new[] { ("投影機", "林雅婷", 3), ("筆記型電腦", "陳家豪", 5), ("相機", "黃怡君", 8), ("延長線", "張志明", 9), ("簡報筆", "李淑芬", 12), ("麥克風", "王建宏", 15) };
        for (var i = 0; i < items.Length; i++)
        {
            sheet.Set(11 + i, 0, items[i].Item1).Set(11 + i, 1, items[i].Item2).Set(11 + i, 2, new DateTime(2025, 10, items[i].Item3), CellStyle.Date);
        }

        corpus.AddOffice("一頁兩表.xlsx", workbook.Build());
    }

    private static void TwoLevelHeader(CorpusBuilder corpus)
    {
        var workbook = new XlsxBuilder();
        var sheet = workbook.AddSheet("區域營收成本");
        sheet.Set("A1", "區域", CellStyle.BoldFill).Merge("A1:A2");
        sheet.Set("B1", "營收", CellStyle.BoldFill).Merge("B1:E1");
        sheet.Set("F1", "成本", CellStyle.BoldFill).Merge("F1:I1");
        for (var q = 0; q < 4; q++)
        {
            sheet.Set(1, 1 + q, $"Q{q + 1}", CellStyle.BoldFill);
            sheet.Set(1, 5 + q, $"Q{q + 1}", CellStyle.BoldFill);
        }

        var data = new (string Region, int[] Revenue, int[] Cost)[]
        {
            ("北區", [4850, 5120, 5420, 6010], [3100, 3300, 3480, 3850]),
            ("中區", [3200, 3380, 3560, 3900], [2150, 2240, 2330, 2540]),
            ("南區", [2800, 2950, 3100, 3420], [1900, 1980, 2070, 2260]),
            ("東區", [1200, 1260, 1330, 1480], [820, 850, 890, 960]),
            ("離島", [380, 410, 450, 520], [290, 300, 330, 370]),
        };
        for (var i = 0; i < data.Length; i++)
        {
            sheet.Set(2 + i, 0, data[i].Region);
            for (var q = 0; q < 4; q++)
            {
                sheet.Set(2 + i, 1 + q, data[i].Revenue[q], CellStyle.Thousands);
                sheet.Set(2 + i, 5 + q, data[i].Cost[q], CellStyle.Thousands);
            }
        }

        corpus.AddOffice("兩層表頭.xlsx", workbook.Build());
    }

    private static void TravelExpense(CorpusBuilder corpus)
    {
        var workbook = new XlsxBuilder();
        var sheet = workbook.AddSheet("差旅費用報告單");
        sheet.Set("A1", "差旅費用報告單", CellStyle.Bold).Merge("A1:C1");
        sheet.Set("A3", "姓名", CellStyle.Bold).Set("B3", "王小明");
        sheet.Set("A4", "部門", CellStyle.Bold).Set("B4", "業務部");
        sheet.Set("A5", "出差地點", CellStyle.Bold).Set("B5", "高雄");
        sheet.Set("A6", "出差日期", CellStyle.Bold).Set("B6", new DateTime(2025, 10, 12), CellStyle.Date);
        sheet.SetRow(7, 0, CellStyle.BoldFill, "項目", "說明", "金額");
        sheet.SetRow(8, 0, CellStyle.Plain, "交通", "高鐵 台北至左營 來回", 1490);
        sheet.SetRow(9, 0, CellStyle.Plain, "住宿", "高雄商務旅館 1 晚", 2300);
        sheet.SetRow(10, 0, CellStyle.Plain, "膳食", "定額核銷", 500);
        sheet.Set(12, 1, "合計", CellStyle.Bold).Set(12, 2, 4290);
        corpus.AddOffice("差旅費用.xlsx", workbook.Build());
    }

    private static void ProjectBudget(CorpusBuilder corpus)
    {
        var workbook = new XlsxBuilder();
        var sheet = workbook.AddSheet("預算");
        sheet.SetRow(0, 0, CellStyle.BoldFill, "項目", "預算", "已支用", "剩餘");
        var items = new (string Name, int Budget, int Spent)[]
        {
            ("硬體採購", 3200000, 2650000),
            ("軟體授權", 1100000, 1100000),
            ("人力成本", 1030000, 640000),
            ("教育訓練", 150000, 30000),
            ("其他", 250000, 88000),
        };
        for (var i = 0; i < items.Length; i++)
        {
            var row = i + 1;
            sheet.Set(row, 0, items[i].Name).Set(row, 1, items[i].Budget, CellStyle.Thousands).Set(row, 2, items[i].Spent, CellStyle.Thousands);
            sheet.Set(row, 3, new FormulaValue($"B{row + 1}-C{row + 1}", items[i].Budget - items[i].Spent), CellStyle.Thousands);
        }

        sheet.Set(6, 0, "總預算", CellStyle.Bold);
        sheet.Set(6, 1, new FormulaValue("SUM(B2:B6)", items.Sum(i => i.Budget)), CellStyle.Thousands);
        sheet.Set(6, 2, new FormulaValue("SUM(C2:C6)", items.Sum(i => i.Spent)), CellStyle.Thousands);
        sheet.Set(6, 3, new FormulaValue("SUM(D2:D6)", items.Sum(i => i.Budget - i.Spent)), CellStyle.Thousands);
        corpus.AddOffice("專案預算表.xlsx", workbook.Build());
    }

    private static void StaffDirectory(CorpusBuilder corpus)
    {
        var random = corpus.Random;
        var workbook = new XlsxBuilder();
        var sheet = workbook.AddSheet("通訊錄");
        sheet.SetRow(0, 0, CellStyle.Bold, "姓名", "部門", "分機", "信箱", "到職日");
        var usedExtensions = new HashSet<int> { 3158 };
        var usedNames = new HashSet<string> { "林雅婷" };
        for (var i = 0; i < 60; i++)
        {
            string name;
            string department;
            int extension;
            if (i == 7)
            {
                name = "林雅婷";
                department = "資訊部";
                extension = 3158;
            }
            else
            {
                do
                {
                    name = Surnames[random.Next(Surnames.Length)] + GivenNames[random.Next(GivenNames.Length)];
                }
                while (!usedNames.Add(name));

                department = Departments[random.Next(Departments.Length)];
                do
                {
                    extension = random.Next(1000, 5000);
                }
                while (!usedExtensions.Add(extension));
            }

            var joined = new DateTime(2015, 1, 1).AddDays(random.Next(0, 3650));
            sheet.Set(i + 1, 0, name).Set(i + 1, 1, department).Set(i + 1, 2, extension).Set(i + 1, 3, $"staff{i + 1:D3}@yaoyang.example").Set(i + 1, 4, joined, CellStyle.Date);
        }

        corpus.AddOffice("員工通訊錄.xlsx", workbook.Build());
    }

    private static void Attendance(CorpusBuilder corpus)
    {
        var random = corpus.Random;
        var workbook = new XlsxBuilder();
        var sheet = workbook.AddSheet("加班統計");
        sheet.Set("A1", "各部門每月加班時數統計（2025 年）", CellStyle.Bold);
        var header = new object?[13];
        header[0] = "部門";
        for (var m = 1; m <= 12; m++)
        {
            header[m] = $"{m}月";
        }

        sheet.SetRow(2, 0, CellStyle.BoldFill, header);
        var departments = new[] { "業務部", "研發部", "資訊部", "財務部", "總務室", "客服中心" };
        for (var d = 0; d < departments.Length; d++)
        {
            sheet.Set(3 + d, 0, departments[d]);
            for (var m = 1; m <= 12; m++)
            {
                var hours = random.Next(20, 160);
                if (d == 0 && m == 10)
                {
                    hours = 186;
                }

                sheet.Set(3 + d, m, hours);
            }
        }

        corpus.AddOffice("出勤統計.xlsx", workbook.Build());
    }
}
