using Contexo.CorpusGen.Office;

namespace Contexo.CorpusGen.Content;

/// <summary>Eight PowerPoint presentations: flow chart with connectors, SmartArt, chart with notes, embedded docx, tables.</summary>
internal static class PresentationDocuments
{
    public static void AddAll(CorpusBuilder corpus)
    {
        PurchaseFlow(corpus);
        OrganisationChart(corpus);
        AnnualReview(corpus);
        EmbeddedDocument(corpus);
        ProductLaunch(corpus);
        SecurityTraining(corpus);
        ProjectSchedule(corpus);
        ServiceProcedure(corpus);
    }

    private static void PurchaseFlow(CorpusBuilder corpus)
    {
        var deck = new PptxBuilder();
        deck.AddSlide().CenteredTitle("請購流程說明");
        var flow = deck.AddSlide().Title("請購流程");
        // Shapes are laid out left to right; the order of the steps is only given by the connectors.
        flow.TextBox(10, 300000, 2000000, 1500000, 800000, "申請人提出請購申請");
        flow.TextBox(11, 2100000, 2000000, 1500000, 800000, "部門主管審核");
        flow.TextBox(12, 3900000, 2000000, 1500000, 800000, "採購部詢價比價");
        flow.TextBox(13, 5700000, 2000000, 1500000, 800000, "財務長核准");
        flow.TextBox(14, 7500000, 2000000, 1500000, 800000, "驗收入庫");
        flow.Connector(20, 10, 11);
        flow.Connector(21, 11, 12);
        flow.Connector(22, 12, 13, label: "金額逾五十萬元");
        flow.Connector(23, 13, 14);
        deck.AddSlide().Title("注意事項").TextBox(30, 457200, 1600200, 8229600, 2000000, "請購單須附上規格與預算科目", "緊急採購須於事後三日內補單");
        corpus.AddOffice("請購流程.pptx", deck.Build());
    }

    private static void OrganisationChart(CorpusBuilder corpus)
    {
        var deck = new PptxBuilder();
        deck.AddSlide().Title("組織架構").SmartArt(
            5,
            ("n1", null, "總經理"),
            ("n2", "n1", "營運長"),
            ("n3", "n1", "財務長"),
            ("n4", "n1", "技術長"),
            ("n5", "n2", "業務部"),
            ("n6", "n4", "研發部"),
            ("n7", "n4", "資訊部"));
        corpus.AddOffice("組織架構.pptx", deck.Build());
    }

    private static void AnnualReview(CorpusBuilder corpus)
    {
        var deck = new PptxBuilder();
        deck.AddSlide().CenteredTitle("2025 年度營運簡報");
        deck.AddSlide()
            .Title("各季營收")
            .Chart(6, 457200, 1500000, "各季營收（百萬元）", ["第一季", "第二季", "第三季", "第四季"], ("營收", [112, 128, 141, 167]))
            .Notes("說明：全年營收較去年成長 14%。", "Q4 重點：強調智慧監控案毛利率 32%，不要提競爭對手報價。");
        deck.AddSlide().Title("明年重點").TextBox(7, 457200, 1600200, 8229600, 2000000, "擴大智慧監控產品線", "導入新版客戶管理系統");
        corpus.AddOffice("2025年度簡報.pptx", deck.Build());
    }

    private static void EmbeddedDocument(CorpusBuilder corpus)
    {
        var risk = new DocxBuilder().AddHeadingStyles();
        risk.Heading(1, "專案風險登記表");
        risk.Para("風險一：測試環境帳號每月 5 日重設，由資訊部統一發放新密碼。");
        risk.Para("風險二：廠商交貨延遲時，專案經理須於兩個工作天內提出替代方案。");
        var riskDocument = ZipNormalizer.Normalize(risk.Build());

        var deck = new PptxBuilder();
        deck.AddSlide().Title("專案風險管理").TextBox(5, 457200, 1600200, 8229600, 1500000, "風險登記表請見內嵌文件").EmbeddedDocx(riskDocument);
        corpus.AddOffice("內嵌docx的簡報.pptx", deck.Build());
    }

    private static void ProductLaunch(CorpusBuilder corpus)
    {
        var deck = new PptxBuilder();
        deck.AddSlide().CenteredTitle("天眼 X2 新產品發表");
        deck.AddSlide().Title("產品規格").TextBox(5, 457200, 1600200, 8229600, 3000000,
            "天眼 X2 網路攝影機",
            "4K 解析度，內建人形偵測",
            "紅外線夜視距離 50 公尺",
            "IP67 防水防塵，適用戶外");
        deck.AddSlide().Title("上市時程").TextBox(6, 457200, 1600200, 8229600, 2000000, "預計 2026 年 3 月開始出貨");
        corpus.AddOffice("新產品發表.pptx", deck.Build());
    }

    private static void SecurityTraining(CorpusBuilder corpus)
    {
        var deck = new PptxBuilder();
        deck.AddSlide().CenteredTitle("資安教育訓練");
        deck.AddSlide().Title("如何辨識釣魚郵件").TextBox(5, 457200, 1600200, 8229600, 3000000,
            "一、檢查寄件者網域是否正確",
            "二、不點擊不明連結與附件",
            "三、有疑慮立即通報資訊部分機 6688");
        corpus.AddOffice("資安教育訓練.pptx", deck.Build());
    }

    private static void ProjectSchedule(CorpusBuilder corpus)
    {
        var deck = new PptxBuilder();
        deck.AddSlide().Title("專案里程碑").Table(
            5, 457200, 1600200, 2,
            [PptxBuilder.SlideSpec.Cell("里程碑"), PptxBuilder.SlideSpec.Cell("日期")],
            [PptxBuilder.SlideSpec.Cell("需求確認"), PptxBuilder.SlideSpec.Cell("2025/09/30")],
            [PptxBuilder.SlideSpec.Cell("系統上線"), PptxBuilder.SlideSpec.Cell("2025/12/15")],
            [PptxBuilder.SlideSpec.Cell("正式驗收"), PptxBuilder.SlideSpec.Cell("2026/01/20")]);
        corpus.AddOffice("專案時程.pptx", deck.Build());
    }

    private static void ServiceProcedure(CorpusBuilder corpus)
    {
        var deck = new PptxBuilder();
        deck.AddSlide().CenteredTitle("客戶服務標準作業程序");
        deck.AddSlide().Title("服務時限").TextBox(5, 457200, 1600200, 8229600, 3000000,
            "客訴案件須於 24 小時內回覆客戶",
            "緊急故障報修須於 2 小時內到場處理",
            "一般報修於三個工作天內完成");
        corpus.AddOffice("客戶服務SOP.pptx", deck.Build());
    }
}
