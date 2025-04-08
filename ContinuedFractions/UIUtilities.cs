using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;

namespace ContinuedFractions
{
    static class UIUtilities
    {
        public static void ShowTopBlock( bool yes, FlowDocument document, Block previousBlock, Block block )
        {
            if( yes )
            {
                if( previousBlock.NextBlock != block ) document.Blocks.InsertAfter( previousBlock, block );
            }
            else
            {
                document.Blocks.Remove( block );
            }
        }
    }
}
