namespace V2_Genesis.Models.Emails
{
    /// <summary>
    /// Section 51 notice email sent to the property owner when a
    /// Third-Party objection is submitted.
    /// </summary>
    public sealed class Section51NoticeEmail
    {
        public string ObjectionNo { get; set; } = string.Empty;
        public string PropertyDescription { get; set; } = string.Empty;
        public string? ValuationKey { get; set; }
        public string RollName { get; set; } = string.Empty;
        public string? Section51Pin { get; set; }
        public DateTime SubmissionsCloseDate { get; set; }
        /// External address (clients).
        public string PortalUrl { get; set; } = string.Empty;

        /// Internal address (City staff on the City network).
        public string? StaffPortalUrl { get; set; }

        /// Owner email from the roll's postal-address table.
        public string OwnerEmail { get; set; } = string.Empty;

        /// False when the roll has no owner email: the notice then goes to the
        /// roll's tracking mailbox only and the PDF must be posted.
        public bool OwnerHasEmail { get; set; } = true;

        /// Where the email actually goes (the owner, the tracking mailbox
        /// when the owner has no email, or the test recipient).
        public string ToAddress { get; set; } = string.Empty;

        /// Valuation Enquiries.
        public List<string> CcAddresses { get; set; } = new();

        /// The roll's tracking mailbox (e.g. GV23Supp4@joburg.org.za) as a blind copy.
        public List<string> BccAddresses { get; set; } = new();
        public bool IsTest { get; set; }

        public byte[] PdfBytes { get; set; } = Array.Empty<byte>();
        public string PdfFileName { get; set; } = string.Empty;

        /// Folder and file name for the .eml copy.
        public string EmlFolderPath { get; set; } = string.Empty;
        public string EmlFileName { get; set; } = string.Empty;

        /// Other folders that get the same .eml copy (the Objection Pack).
        public List<string> EmlExtraFolders { get; set; } = new();
    }
}
