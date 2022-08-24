using System;
using System.Collections.Generic;
using System.Text;

namespace Smeargle.Configuration
{
    public class InvisionCommunityConfiguration
    {
        public string ApiKey { get; set; } = default!;
        public string BaseUrl { get; set; } = default!;
        public int GalleryCategoryId { get; set; }
    }
}
