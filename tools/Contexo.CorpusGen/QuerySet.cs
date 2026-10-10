namespace Contexo.CorpusGen;

/// <summary>The evaluation queries. Each one names the file(s) whose content answers it.</summary>
internal static class QuerySet
{
    public static void AddAll(CorpusBuilder c)
    {
        // Office documents
        c.AddQuery("q01", "監視系統的報價金額", "2025_台中案_報價單.xlsx", "form");
        c.AddQuery("q02", "採購驗收的標準是什麼", "採購規範.docx", "heading", location: "驗收");
        c.AddQuery("q03", "申請之後要給誰審核", "請購流程.pptx", "diagram");
        // The analysis question from the task description. A 3,000-row table is only represented by its summary chunk
        // (file, columns, five sample rows), so a question that names none of those is hard to match: observed, not gated (see q43).
        c.AddQuery("q04", "哪個客戶去年下單金額最高", "銷售明細.xlsx", "table-analysis", needsTable: true, observeOnly: true);
        c.AddQuery("q05", "報價", ["2025_台中案_報價單.xlsx", "智慧監控提案書.docx"], "short");
        c.AddQuery("q06", "ABC-123", "設備維護手冊.docx", "model-number");
        c.AddQuery("q07", "UPS battery replacement interval", "設備維護手冊.docx", "english");
        c.AddQuery("q08", "台中案預算核定多少", "會議紀錄_2025Q3營運會議.docx", "tracked-change");
        c.AddQuery("q09", "合約的保固期間多久", "服務合約範本.docx", "footnote");
        c.AddQuery("q10", "智慧監控提案的總價", "智慧監控提案書.docx", "embedded-host");
        c.AddQuery("q11", "結婚可以請幾天假", "員工手冊.docx", "paraphrase", location: "婚假");
        c.AddQuery("q12", "密碼多久要換一次", "資訊安全政策.docx", "paraphrase");
        c.AddQuery("q13", "出差住宿一晚最多報多少", "出差旅費辦法.docx", "paraphrase");
        c.AddQuery("q14", "供應商幾分算 A 級", "供應商評鑑辦法.docx", "table-in-doc");
        c.AddQuery("q15", "新進人員要上幾小時的訓練", "教育訓練計畫.docx", "paraphrase");
        c.AddQuery("q16", "資訊部歸誰管", "組織架構.pptx", "smartart");
        c.AddQuery("q17", "智慧監控案的毛利率", "2025年度簡報.pptx", "notes");
        c.AddQuery("q18", "第四季營收是多少", "2025年度簡報.pptx", "chart");
        c.AddQuery("q19", "測試環境帳號何時重設", "內嵌docx的簡報.pptx", "embedded");
        c.AddQuery("q20", "攝影機的夜視距離多遠", "新產品發表.pptx", "slide");
        c.AddQuery("q21", "收到可疑郵件要通報哪個分機", "資安教育訓練.pptx", "slide");
        c.AddQuery("q22", "系統上線日期", "專案時程.pptx", "slide-table");
        c.AddQuery("q23", "客戶報修多久內要到場", "客戶服務SOP.pptx", "slide");
        c.AddQuery("q24", "客戶清單裡有多少家北部客戶", "客戶清單.xlsx", "table", needsTable: true);
        c.AddQuery("q25", "XYZ-789 庫存", "庫存清單.xlsx", "model-number");
        c.AddQuery("q26", "研發部有幾個人", "一頁兩表.xlsx", "two-tables");
        c.AddQuery("q27", "北區第三季營收", "兩層表頭.xlsx", "two-level-header");
        c.AddQuery("q28", "高雄出差的高鐵費用", "差旅費用.xlsx", "form");
        c.AddQuery("q29", "專案預算表的總預算", "專案預算表.xlsx", "formula");
        c.AddQuery("q30", "林雅婷的分機", "員工通訊錄.xlsx", "lookup");
        c.AddQuery("q31", "業務部十月加班時數", "出勤統計.xlsx", "lookup");

        // PDF (English content)
        c.AddQuery("q32", "When are fire drills held", "Workplace_Safety_Regulations.pdf", "english");
        c.AddQuery("q33", "how long is CCTV footage kept", "Records_Retention_Standard.pdf", "english");
        c.AddQuery("q34", "how many days a week can I work from home", "Remote_Work_Guidelines.pdf", "english");
        c.AddQuery("q35", "gift reporting threshold", "Employee_Code_of_Conduct.pdf", "english");

        // Text, Markdown, HTML, CSV
        c.AddQuery("q36", "停車證怎麼換", "舊版公告_停車證換發.txt", "big5");
        c.AddQuery("q37", "會議室最多可以借幾小時", "會議室使用規則.txt", "big5");
        c.AddQuery("q38", "訪客 Wi-Fi 密碼多久換", "新人手冊.md", "markdown");
        c.AddQuery("q39", "什麼時候停電", "公告_停電通知.html", "html");
        c.AddQuery("q40", "尾牙在哪裡舉行", "公告_尾牙.html", "html");
        c.AddQuery("q41", "訂單匯出檔裡已出貨的訂單有幾筆", "客戶訂單匯出.csv", "table", needsTable: true);
        c.AddQuery("q42", "資訊部值班手機", "緊急聯絡人.csv", "lookup");
        c.AddQuery("q43", "銷售明細表", "銷售明細.xlsx", "table", needsTable: true);

        // Observed only: the answer sits inside a large table that is queried with SQL, or inside an embedded large table.
        c.AddQuery("o01", "PTZ-2000 球型攝影機的單價", "智慧監控提案書.docx", "embedded-table", observeOnly: true);
        // Excel shows 1,285,000; the parser stores the number as 1285000 (task T08: no thousands separator).
        c.AddQuery("o02", "1,285,000", "2025_台中案_報價單.xlsx", "formatted-number", observeOnly: true);
        c.AddQuery("o03", "晨星科技去年買了多少錢", "銷售明細.xlsx", "table", needsTable: true, observeOnly: true);
    }
}
