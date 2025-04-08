using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace ContinuedFractionsLibrary
{
    public static class ContinuedFractionUtilities
    {
        public static IEnumerable<BigInteger> EnumerateContinuedFraction( BigInteger n, BigInteger d )
        {
            if( n < 0 ) throw new ArgumentException( "Negative nominator", nameof( n ) );
            //if( d.IsZero ) throw new ArgumentException( "Zero denominator", nameof( d ) );

            for(; ; )
            {
                (BigInteger q, BigInteger r) = BigInteger.DivRem( n, d );

                yield return q;

                if( r.IsZero ) break;

                n = d;
                d = r;
            }
        }

        public static IEnumerable<(BigInteger n, BigInteger d)> EnumerateContinuedFractionConvergents( IReadOnlyList<BigInteger> continuedFraction )
        {
            // https://r-knott.surrey.ac.uk/Fibonacci/cfINTRO.html#convergrecurr

            if( continuedFraction.Count <= 0 ) throw new ArgumentException( "Empty continued fraction", nameof( continuedFraction ) );

            BigInteger prev_n = BigInteger.One;
            BigInteger prev_d = BigInteger.Zero;
            BigInteger n = continuedFraction[0];
            BigInteger d = BigInteger.One;

            yield return (n, d);

            for( int i = 1; i < continuedFraction.Count; i++ )
            {
                BigInteger new_n = continuedFraction[i] * n + prev_n;
                BigInteger new_d = continuedFraction[i] * d + prev_d;

                //Debug.WriteLine( $"{new_n:D}/{new_d:D}" );

                prev_n = n;
                prev_d = d;
                n = new_n;
                d = new_d;

                yield return (n, d);
            }
        }
    }
}
