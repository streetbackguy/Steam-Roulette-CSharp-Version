using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SteamRoulette.Models
{
    public class ProgressReport
    {
        public int Current { get; set; }
        public int Total { get; set; }
        public string Message { get; set; } = "";
    }
}
