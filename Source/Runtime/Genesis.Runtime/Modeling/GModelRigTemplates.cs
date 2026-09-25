using System;

namespace Genesis.Runtime.Modeling
{
    public static class GModelRigTemplates
    {
        public static readonly string[] Categories = { "Humanoid", "Quadruped" };

        public static string[] Subcategories(string category)
        {
            if (string.Equals(category, "Humanoid", StringComparison.OrdinalIgnoreCase))
                return new[] { "Generic" };
            if (string.Equals(category, "Quadruped", StringComparison.OrdinalIgnoreCase))
                return new[] { "Fox", "Dog", "Wolf", "Cat" };
            return new[] { "Generic" };
        }

        public static string ComposeId(string category, string subcategory)
            => $"{category.Trim()}/{subcategory.Trim()}";

        public static void ParseId(string templateId, out string category, out string subcategory)
        {
            category = "Quadruped";
            subcategory = "Fox";
            if (string.IsNullOrWhiteSpace(templateId))
                return;

            int slash = templateId.IndexOf('/');
            if (slash < 0)
            {
                if (templateId.Equals("Humanoid", StringComparison.OrdinalIgnoreCase))
                {
                    category = "Humanoid";
                    subcategory = "Generic";
                }
                else if (templateId.Equals("Quadruped", StringComparison.OrdinalIgnoreCase))
                {
                    category = "Quadruped";
                    subcategory = "Generic";
                }
                else
                {
                    category = "Quadruped";
                    subcategory = templateId;
                }
                return;
            }

            category = templateId[..slash].Trim();
            subcategory = templateId[(slash + 1)..].Trim();
        }
    }
}
