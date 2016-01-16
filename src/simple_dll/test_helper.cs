using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace simple_dll
{
    public static class TestHelper
    {
        public static string ThowException()
        {
            throw new Exception("exception on purpose");
        }
    }
}
