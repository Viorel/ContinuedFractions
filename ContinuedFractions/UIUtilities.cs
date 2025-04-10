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
        public static void ShowTopBlock( FlowDocument document, Block block, bool yes, Block previousBlock )
        {
            if( yes )
            {
                if( block.Parent == null )
                {
                    document.Blocks.InsertAfter( previousBlock, block );
                }
            }
            else
            {
                document.Blocks.Remove( block );
            }
        }

        public static void ShowTopBlock( FlowDocument document, Block block, bool yes, params Block[] previousBlocks )
        {
            if( yes )
            {
                if( block.Parent == null )
                {
                    Block found_previous_block = previousBlocks.Last( b => b.Parent != null );

                    document.Blocks.InsertAfter( found_previous_block, block );
                }
            }
            else
            {
                document.Blocks.Remove( block );
            }
        }
    }
}
