using Contexo.CorpusGen.Office;

namespace Contexo.CorpusGen.Content;

/// <summary>Ten Word documents of the fictional company 曜陽科技股份有限公司.</summary>
internal static class WordDocuments
{
    private const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static void AddAll(CorpusBuilder corpus)
    {
        Procurement(corpus);
        MeetingMinutes(corpus);
        ServiceContract(corpus);
        Proposal(corpus);
        EmployeeHandbook(corpus);
        SecurityPolicy(corpus);
        TravelPolicy(corpus);
        SupplierEvaluation(corpus);
        TrainingPlan(corpus);
        MaintenanceManual(corpus);
    }

    private static DocxBuilder New(string title)
    {
        var doc = new DocxBuilder().AddHeadingStyles();
        doc.Heading(1, title);
        return doc;
    }

    private static string Cells(params string[] texts) => string.Concat(texts.Select(t => DocxBuilder.Cell(t)));

    private static void Procurement(CorpusBuilder corpus)
    {
        var doc = New("採購規範");
        doc.Para("曜陽科技股份有限公司 文件編號 PUR-001 第 3 版");
        doc.Heading(2, "第一章 總則");
        doc.Para("本規範用以確保公司採購作業公平、透明且符合成本效益，適用於本公司所有新台幣一萬元以上之採購案件。");
        doc.Heading(2, "第二章 請購與比價");
        doc.Para("採購金額在新台幣五萬元以下者，得向單一廠商議價採購。");
        doc.Para("採購金額逾五萬元至五十萬元者，須取得三家以上廠商報價並完成比價紀錄。");
        doc.Para("採購金額逾五十萬元者，應公開招標，並經採購委員會核准後始得簽約。");
        doc.Heading(2, "第三章 驗收");
        doc.Heading(3, "3.1 驗收期限");
        doc.Para("驗收應於貨品到達後七個工作天內完成，逾期未驗收視為合格，但不影響本公司之後的索賠權利。");
        doc.Heading(3, "3.2 驗收標準");
        doc.Para("驗收人員應依下表之項目逐項檢查，並於驗收單上簽名。外觀檢查以抽樣方式進行，不良率超過百分之二者，整批退回並通知廠商於十個工作天內補貨。");
        doc.Table(
            DocxBuilder.Row(Cells("驗收項目", "抽樣比例", "允收標準"), header: true) +
            DocxBuilder.Row(Cells("外觀檢查", "10%", "不良率不超過 2%")) +
            DocxBuilder.Row(Cells("功能測試", "100%", "全數通過")) +
            DocxBuilder.Row(Cells("文件齊全", "100%", "保固書、操作手冊、原廠證明各一份")));
        doc.Heading(2, "第四章 付款");
        doc.Para("驗收合格後，本公司於收到發票之次月三十日前以匯款方式支付貨款。");
        corpus.AddOffice("採購規範.docx", doc.Build());
    }

    private static void MeetingMinutes(CorpusBuilder corpus)
    {
        var doc = New("2025 年第三季營運會議紀錄");
        doc.Para("會議時間：2025 年 9 月 26 日 下午二時　地點：十樓大會議室");
        doc.Para("出席人員：總經理、營運長、財務長、技術長、各部門主管");
        doc.Heading(2, "議題一：台中案預算");
        // Tracked change: the old figure was deleted and the new one inserted.
        doc.Xml(
            DocxBuilder.RunXml("經討論，台中案預算由 ") +
            "<w:del w:id=\"101\" w:author=\"財務長\" w:date=\"2025-09-27T09:00:00Z\"><w:r><w:delText>380</w:delText></w:r></w:del>" +
            "<w:ins w:id=\"102\" w:author=\"財務長\" w:date=\"2025-09-27T09:00:00Z\">" + DocxBuilder.RunXml("420") + "</w:ins>" +
            DocxBuilder.RunXml(" 萬元核定，並於十月底前完成招標文件。"));
        doc.Heading(2, "議題二：倉庫搬遷");
        doc.Para("倉庫搬遷訂於 10 月 18 日（六）進行，各部門須於 10 月 15 日前清點並貼上標籤。");
        doc.Heading(2, "議題三：資安演練");
        doc.Para("全公司資安演練訂於 11 月 7 日（五）上午舉行，由資訊部負責寄送測試郵件。");
        doc.Heading(2, "臨時動議");
        doc.Para("無。");
        corpus.AddOffice("會議紀錄_2025Q3營運會議.docx", doc.Build());
    }

    private static void ServiceContract(CorpusBuilder corpus)
    {
        var doc = New("設備維護服務合約範本");
        doc.AddFootnotes(
            (1, "保固範圍不含人為損壞、天災及未經授權之改裝。"),
            (2, "違約金之計算以合約總價為基準，不含稅額。"));
        doc.Para("立合約書人：甲方（曜陽科技股份有限公司）與乙方（承包廠商），茲就設備維護服務事宜訂立本合約，條款如下。");
        doc.Heading(2, "第一條 服務範圍");
        doc.Para("乙方應提供甲方所指定設備之定期巡檢、故障排除及零件更換服務。");
        doc.Heading(2, "第五條 保固");
        doc.Xml(
            DocxBuilder.RunXml("設備自驗收合格之日起算，保固期間為二十四個月。") +
            "<w:r><w:footnoteReference w:id=\"1\"/></w:r>" +
            DocxBuilder.RunXml("保固期間內因設備本身瑕疵所生之維修費用，由乙方負擔。"));
        doc.Heading(2, "第七條 違約金");
        doc.Xml(
            DocxBuilder.RunXml("乙方逾期履約者，每逾一日應按合約總價千分之三計算違約金，但以合約總價百分之二十為上限。") +
            "<w:r><w:footnoteReference w:id=\"2\"/></w:r>");
        doc.Heading(2, "第九條 付款");
        doc.Para("甲方應於驗收合格後三十日內，以匯款方式支付合約價款。");
        doc.Heading(2, "第十二條 管轄");
        doc.Para("因本合約所生之爭議，雙方同意以台灣台北地方法院為第一審管轄法院。");
        corpus.AddOffice("服務合約範本.docx", doc.Build());
    }

    private static void Proposal(CorpusBuilder corpus)
    {
        var doc = New("智慧監控系統建置提案書");
        // The detail table lives in an embedded workbook: 60 rows x 8 columns = 480 cells, i.e. a "large" table.
        var workbook = EmbeddedQuotation(corpus);
        var relationship = doc.AddEmbeddedPackage(workbook, XlsxType);
        doc.Heading(2, "壹、專案背景");
        doc.Para("客戶現有監視設備老舊，影像解析度不足且無法遠端調閱，擬汰換為具備人形偵測的智慧監控系統。");
        doc.Heading(2, "貳、解決方案");
        doc.Para("本提案採用 4K 網路攝影機搭配集中式錄影主機，重要出入口加裝球型攝影機，並提供行動裝置即時檢視。");
        doc.Heading(2, "參、建置時程");
        doc.Para("簽約後第 1 至 2 週完成現場勘查，第 3 至 6 週進行設備安裝與佈線，第 7 週教育訓練，第 8 週驗收。");
        doc.Heading(2, "肆、預算概要");
        doc.Para("本案提案總價為新台幣 3,860,000 元整（含稅），付款條件為簽約 30%、到貨 40%、驗收 30%。");
        doc.Para("附件一：硬體報價明細（內嵌試算表，共 60 項）。");
        doc.Xml($"<w:r><w:object><o:OLEObject Type=\"Embed\" ProgID=\"Excel.Sheet.12\" r:id=\"{relationship}\"/></w:object></w:r>");
        corpus.AddOffice("智慧監控提案書.docx", doc.Build());
    }

    private static byte[] EmbeddedQuotation(CorpusBuilder corpus)
    {
        var workbook = new XlsxBuilder();
        var sheet = workbook.AddSheet("報價明細");
        sheet.SetRow(0, 0, CellStyle.Bold, "項次", "品名", "規格", "單位", "數量", "單價", "小計", "備註");
        var items = new (string Name, string Spec, int Price)[]
        {
            ("4K 固定式攝影機", "FX-400", 8200),
            ("PTZ-2000 球型攝影機", "PTZ-2000", 23500),
            ("網路錄影主機 32 路", "NVR-32", 68000),
            ("PoE 交換器 24 埠", "SW-24P", 15800),
            ("硬碟 8TB", "HDD-8T", 6900),
            ("光纖收發器", "FC-10G", 2100),
        };
        for (var i = 0; i < 60; i++)
        {
            var item = items[i % items.Length];
            var quantity = 1 + (i % 4);
            var row = i + 1;
            sheet.SetRow(row, 0, CellStyle.Plain, i + 1, i < items.Length ? item.Name : $"{item.Name} 第 {(i / items.Length) + 1} 區", item.Spec, "台", quantity, item.Price, quantity * item.Price, "");
        }

        return ZipNormalizer.Normalize(workbook.Build());
    }

    private static void EmployeeHandbook(CorpusBuilder corpus)
    {
        var doc = New("員工手冊");
        doc.Heading(2, "第一章 工作時間");
        doc.Para("正常工作時間為每日上午九時至下午六時，中午十二時至十三時為午休時間。");
        doc.Heading(2, "第二章 請假規定");
        doc.Heading(3, "2.1 特別休假");
        doc.Para("到職滿一年者給予特別休假七日，滿二年者十日，滿三年者十四日。");
        doc.Heading(3, "2.2 病假與事假");
        doc.Para("病假每年以三十日為限，未滿一個月者折半給薪；事假每年十四日，不給薪。");
        doc.Heading(3, "2.3 婚假與喪假");
        doc.Para("員工結婚者給予婚假八日，工資照給，應於結婚之日前十日內請畢。");
        doc.Para("員工之父母、配偶喪亡者給予喪假八日，祖父母、子女喪亡者六日。");
        doc.Heading(2, "第三章 福利");
        doc.Para("公司提供年度健康檢查補助，每人每年新台幣三千元；生日當月可領取禮券五百元。");
        corpus.AddOffice("員工手冊.docx", doc.Build());
    }

    private static void SecurityPolicy(CorpusBuilder corpus)
    {
        var doc = New("資訊安全政策");
        doc.Heading(2, "一、帳號與密碼");
        doc.Para("所有系統密碼長度至少十二碼，須包含大小寫英文字母與數字，並且每九十天更換一次。");
        doc.Para("同一組密碼不得重複用於公司以外的網站，亦不得以明文方式記錄於便利貼或共用文件。");
        doc.Heading(2, "二、設備管理");
        doc.Para("公司配發之筆記型電腦須啟用磁碟加密，離開座位時須鎖定螢幕。");
        doc.Heading(2, "三、人員異動");
        doc.Para("員工離職當日，資訊部應立即停用其所有系統帳號與電子郵件，並回收公司設備。");
        doc.Heading(2, "四、事件通報");
        doc.Para("發現可疑郵件或疑似資安事件，應於一小時內通報資訊部。");
        corpus.AddOffice("資訊安全政策.docx", doc.Build());
    }

    private static void TravelPolicy(CorpusBuilder corpus)
    {
        var doc = New("出差旅費辦法");
        doc.Heading(2, "一、適用範圍");
        doc.Para("本辦法適用於因公務需要前往公司所在縣市以外地區出差之員工。");
        doc.Heading(2, "二、交通費");
        doc.Para("國內出差以高鐵標準車廂或台鐵自強號為原則，搭乘飛機須事先經部門主管核准。");
        doc.Heading(2, "三、住宿與膳食");
        doc.Para("國內出差住宿費每晚上限新台幣二千五百元，須檢附發票；膳食費每日新台幣五百元，採定額核銷。");
        doc.Heading(2, "四、核銷期限");
        doc.Para("出差結束後七個工作天內，應填寫差旅費用報告並附上單據送交財務部。");
        corpus.AddOffice("出差旅費辦法.docx", doc.Build());
    }

    private static void SupplierEvaluation(CorpusBuilder corpus)
    {
        var doc = New("供應商評鑑辦法");
        doc.Heading(2, "一、評鑑項目");
        doc.Para("每年十二月就品質、交期、價格、服務四個面向對主要供應商進行評分，滿分一百分。");
        doc.Heading(2, "二、等級劃分");
        doc.Table(
            DocxBuilder.Row(Cells("分數", "等級", "處理方式"), header: true) +
            DocxBuilder.Row(Cells("90 分以上", "A 級", "列為優先採購廠商")) +
            DocxBuilder.Row(Cells("75 至 89 分", "B 級", "維持合作並要求改善")) +
            DocxBuilder.Row(Cells("60 至 74 分", "C 級", "限期改善，列入觀察")) +
            DocxBuilder.Row(Cells("未滿 60 分", "D 級", "暫停交易")));
        doc.Heading(2, "三、複評");
        doc.Para("C 級廠商須於六個月內完成改善並接受複評，複評仍未通過者降為 D 級。");
        corpus.AddOffice("供應商評鑑辦法.docx", doc.Build());
    }

    private static void TrainingPlan(CorpusBuilder corpus)
    {
        var doc = New("教育訓練計畫");
        doc.Heading(2, "一、新進人員訓練");
        doc.Para("新進人員到職後一個月內須完成十六小時的新人訓練，內容包含公司簡介、資訊安全與職場安全。");
        doc.Heading(2, "二、年度必修課程");
        doc.Table(
            DocxBuilder.Row(Cells("課程", "對象", "時數"), header: true) +
            DocxBuilder.Row(Cells("資訊安全", "全體員工", "3 小時")) +
            DocxBuilder.Row(Cells("職場安全衛生", "全體員工", "2 小時")) +
            DocxBuilder.Row(Cells("專案管理", "專案經理", "8 小時")));
        doc.Heading(2, "三、訓練經費");
        doc.Para("每位員工每年教育訓練經費上限為新台幣一萬二千元，超過者須事先簽核。");
        corpus.AddOffice("教育訓練計畫.docx", doc.Build());
    }

    private static void MaintenanceManual(CorpusBuilder corpus)
    {
        var doc = New("設備維護手冊");
        doc.Heading(2, "一、空調設備");
        doc.Para("辦公室冷氣機的濾網每三個月清洗一次，夏季使用前須由總務室安排專業廠商保養。");
        doc.Heading(2, "二、不斷電系統");
        doc.Para("機房使用型號 ABC-123 不斷電系統（UPS），電池每兩年更換一次，更換前須先通知資訊部關閉非必要設備。");
        doc.Heading(2, "三、飲水機");
        doc.Para("飲水機濾心每半年更換一次，並於設備上黏貼更換日期標籤。");
        doc.Heading(2, "Quick reference");
        doc.Para("Replace the UPS battery every 2 years. Clean air conditioner filters every 3 months. Report any equipment failure to the General Affairs office.");
        corpus.AddOffice("設備維護手冊.docx", doc.Build());
    }
}
