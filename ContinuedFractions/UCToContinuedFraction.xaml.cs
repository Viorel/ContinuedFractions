using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Documents.DocumentStructures;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using ContinuedFractionsLibrary;

namespace ContinuedFractions
{
    /// <summary>
    /// Interaction logic for UCToContinuedFraction.xaml
    /// </summary>
    public partial class UCToContinuedFraction : UserControl
    {
        const int MAX_CONTINUED_FRACTION_ITEMS = 100;
        const int MAX_BIGINTEGER_BYTE_SIZE = 128;
        readonly TimeSpan DELAY_BEFORE_CALCULATION = TimeSpan.FromMilliseconds( 444 );
        readonly TimeSpan DELAY_BEFORE_PROGRESS = TimeSpan.FromMilliseconds( 455 ); // (must be greater than 'DELAY_BEFORE_CALCULATION')
        readonly TimeSpan MIN_DURATION_PROGRESS = TimeSpan.FromMilliseconds( 444 );

        bool mLoaded = false;
        bool mIsRestoreError = false;
        readonly DispatcherTimer mCalculationTimer;
        Thread? mCalculationThread = null;
        SimpleCancellable? mLastCancellable = null;
        readonly DispatcherTimer mProgressTimer = new( );
        DateTime mProgressShownTime = DateTime.MinValue;
        enum ProgressStatusEnum { None, DelayToShow, DelayToHide };
        ProgressStatusEnum mProgressStatus = ProgressStatusEnum.None;

        public UCToContinuedFraction( )
        {
            InitializeComponent( );

            richTextBoxNote.Visibility = Visibility.Visible;
            richTextBoxTypicalError.Visibility = Visibility.Hidden;
            richTextBoxError.Visibility = Visibility.Hidden;
            richTextBoxResults.Visibility = Visibility.Hidden;
            labelPleaseWait.Visibility = Visibility.Hidden;

            mCalculationTimer = new DispatcherTimer
            {
                Interval = DELAY_BEFORE_CALCULATION,
            };
            mCalculationTimer.Tick += CalculationTimer_Tick;

            mProgressTimer.Tick += ProgressTimer_Tick;
        }

        private void UserControl_Loaded( object sender, RoutedEventArgs e )
        {
            // it seems to be called multiple times

            if( !mLoaded )
            {
                mLoaded = true;

                ApplySavedData( );
            }
        }

        private void textBoxNumber_TextChanged( object sender, TextChangedEventArgs e )
        {
            if( !mLoaded ) return;

            RestartCalculationTimer( );
        }

        private void textBoxNumber_SelectionChanged( object sender, RoutedEventArgs e )
        {
            if( !mLoaded ) return;

            PostponeCalculationTimer( );
        }

        private void CalculationTimer_Tick( object? sender, EventArgs e )
        {
            mCalculationTimer.Stop( );

            RestartCalculations( );
        }

        void RestartCalculationTimer( )
        {
            mCalculationTimer.Stop( );
            mCalculationTimer.Start( );
            ShowProgress( );
        }

        void PostponeCalculationTimer( )
        {
            if( mCalculationTimer.IsEnabled ) RestartCalculationTimer( );
        }

        void ApplySavedData( )
        {
            try
            {
                textBoxNumber.Text = Properties.Settings.Default.LastInput;

                textBoxNumber.Focus( );
                textBoxNumber.SelectAll( );
            }
            catch( Exception exc )
            {
                mIsRestoreError = true;

                if( Debugger.IsAttached ) Debugger.Break( );
                else Debug.Fail( exc.Message, exc.ToString( ) );

                // ignore
            }
        }

        internal void SaveData( )
        {
            if( !mIsRestoreError ) // avoid overwriting details in case of errors
            {
                Properties.Settings.Default.LastInput = textBoxNumber.Text;

                Properties.Settings.Default.Save( );
            }
        }

        internal void Stop( )
        {
            mCalculationTimer.Stop( );
            StopThread( );
        }

        void StopThread( )
        {
            try
            {
                if( mCalculationThread != null )
                {
                    mLastCancellable?.SetCancel( );
                    mCalculationThread.Interrupt( );
                    mCalculationThread.Join( 99 );
                    mCalculationThread = null;
                }
            }
            catch( Exception exc )
            {
                if( Debugger.IsAttached ) Debugger.Break( );
                else Debug.Fail( exc.Message, exc.ToString( ) );

                // ignore?
            }
        }

        void RestartCalculations( )
        {
            try
            {
                StopThread( );

                Fraction? fraction = GetInputNumber( );
                if( fraction == null ) return;

                mLastCancellable = new SimpleCancellable( );
                mCalculationThread = new Thread( ( ) =>
                {
                    CalculationThreadProc( mLastCancellable, fraction );
                } )
                {
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal
                };

                mCalculationThread.Start( );
            }
            catch( Exception exc )
            {
                //if( Debugger.IsAttached ) Debugger.Break( );

                string error_text = $"Something went wrong.\r\n\r\n{exc.Message}";
                if( Debugger.IsAttached ) error_text = $"{error_text}\r\n\r\n{exc.StackTrace}";

                ShowError( error_text );
            }
        }

        Fraction? GetInputNumber( )
        {
            string input_text = textBoxNumber.Text;

            if( string.IsNullOrWhiteSpace( input_text ) )
            {
                ShowOneRichTextBox( richTextBoxNote );
                HideProgress( );

                return null;
            }

            // TODO: trim insignificant zeroes (in Regex)

            Match m = RegexToParseNumber( ).Match( input_text );

            if( m.Groups["integer"].Success )
            {
                bool is_negative = m.Groups["negative"].Success;
                bool is_exponent_negative = m.Groups["negative_exponent"].Success;
                Group floating_group = m.Groups["floating"];
                Group repeating_group = m.Groups["repeating"];
                Group exponent_group = m.Groups["exponent"];

                BigInteger integer = BigInteger.Parse( m.Groups["integer"].Value, CultureInfo.InvariantCulture );
                BigInteger exponent = exponent_group.Success ? BigInteger.Parse( exponent_group.Value, CultureInfo.InvariantCulture ) : BigInteger.Zero;
                if( is_exponent_negative ) exponent = -exponent;

                if( floating_group.Success || repeating_group.Success )
                {
                    // 123.45, 123.45(67), 123.(67), maybe with e

                    BigInteger floating = floating_group.Success ? BigInteger.Parse( floating_group.Value, CultureInfo.InvariantCulture ) : BigInteger.Zero;
                    int floating_length = floating_group.Success ? floating_group.Value.Length : 0;
                    BigInteger floating_magnitude = BigInteger.Pow( 10, floating_length );

                    if( repeating_group.Success )
                    {
                        // 123.45(67), 123.(67), maybe with e

                        BigInteger repeating = BigInteger.Parse( repeating_group.Value, CultureInfo.InvariantCulture );
                        int repeating_length = repeating_group.Value.Length;

                        BigInteger repeating_magnitude = BigInteger.Pow( 10, repeating_length );

                        BigInteger significant = integer * floating_magnitude + floating;
                        BigInteger significant_with_repeating = significant * repeating_magnitude + repeating;
                        Debug.Assert( significant_with_repeating >= significant );
                        BigInteger nominator = significant_with_repeating - significant;
                        BigInteger denominator = floating_magnitude * ( repeating_magnitude - 1 );

                        Fraction fraction = new( is_negative ? -nominator : nominator, denominator, exponent );

                        return fraction;
                    }
                    else
                    {
                        // 123.45, maybe with e

                        BigInteger significant = integer * floating_magnitude + floating;
                        BigInteger adjusted_exponent = exponent - floating_length;

                        Fraction fraction = new( is_negative ? -significant : significant, BigInteger.One, adjusted_exponent );

                        return fraction;
                    }
                }
                else
                {
                    // 123, 123e45

                    Fraction fraction = new( is_negative ? -integer : integer, BigInteger.One, exponent );

                    return fraction;
                }
            }

            if( m.Groups["nominator"].Success )
            {
                bool is_negative = m.Groups["negative"].Success;
                bool is_exponent_negative = m.Groups["negative_exponent"].Success;
                Group denominator_group = m.Groups["denominator"];
                Group exponent_group = m.Groups["exponent"];

                BigInteger nominator = BigInteger.Parse( m.Groups["nominator"].Value, CultureInfo.InvariantCulture );
                BigInteger denominator = denominator_group.Success ? BigInteger.Parse( denominator_group.Value, CultureInfo.InvariantCulture ) : BigInteger.One;
                BigInteger exponent = exponent_group.Success ? BigInteger.Parse( exponent_group.Value, CultureInfo.InvariantCulture ) : BigInteger.Zero;
                if( is_exponent_negative ) exponent = -exponent;

                Fraction fraction;

                if( nominator.IsZero )
                {
                    if( denominator.IsZero )
                    {
                        fraction = Fraction.Undefined;
                    }
                    else
                    {
                        fraction = Fraction.Zero;
                    }
                }
                else
                {
                    if( denominator.IsZero )
                    {
                        fraction = is_negative ? Fraction.NegativeInfinity : Fraction.PositiveInfinity;
                    }
                    else
                    {
                        fraction = new Fraction( is_negative ? -nominator : nominator, denominator, exponent );
                    }
                }

                return fraction;
            }

            if( m.Groups["pi"].Success )
            {
                return Fraction.Pi;
            }

            if( m.Groups["e"].Success )
            {
                return Fraction.EulerNumber;
            }

            ShowOneRichTextBox( richTextBoxTypicalError );
            HideProgress( );

            return null;
        }

        void CalculationThreadProc( ICancellable cnc, Fraction fraction )
        {
            try
            {
                if( !fraction.IsNormal )
                {
                    ShowResults( cnc, fraction, [] );
                    HideProgress( );
                }
                else
                {
                    CalculationContext ctx = new( cnc, 33 );

                    fraction = fraction.Simplify( ctx );

                    (BigInteger n, BigInteger d, BigInteger e) = Fraction.Abs( fraction, ctx ).ToNDE( );

                    string? error_text = null;

                    while( error_text == null && e < 0 )
                    {
                        d *= 10;
                        ++e;

                        if( d.GetByteCount( ) > MAX_BIGINTEGER_BYTE_SIZE )
                        {
                            error_text = "The number exceeds the supported limits.";
                        }
                    }

                    while( error_text == null && e > 0 )
                    {
                        n *= 10;
                        --e;

                        if( n.GetByteCount( ) > MAX_BIGINTEGER_BYTE_SIZE )
                        {
                            error_text = "The number exceeds the supported limits.";
                        }
                    }

                    if( error_text == null )
                    {
                        BigInteger[] continued_fraction_items =
                            [.. ContinuedFractionUtilities
                            .EnumerateContinuedFraction( n, d )
                            .Take( MAX_CONTINUED_FRACTION_ITEMS + 1 )];

                        if( fraction.IsNegative ) continued_fraction_items = ContinuedFractionUtilities.Negate( continued_fraction_items );

                        ShowResults( cnc, fraction, continued_fraction_items );
                        HideProgress( );
                    }

                    if( !string.IsNullOrEmpty( error_text ) )
                    {
                        ShowError( error_text );
                    }
                }
            }
            catch( OperationCanceledException ) // also 'TaskCanceledException'
            {
                // (the operation is supposed to be restarted)
                return;
            }
            catch( Exception exc )
            {
                //if( Debugger.IsAttached ) Debugger.Break( );

                string error_text = $"Something went wrong.\r\n\r\n{exc.Message}";
                if( Debugger.IsAttached ) error_text = $"{error_text}\r\n\r\n{exc.StackTrace}";

                ShowError( error_text );
            }
        }

        void ShowResults( ICancellable cnc, Fraction initialFraction, BigInteger[] continued_fraction_items )
        {
            StringBuilder sb_continued_fraction = new( );
            string decimal_string;
            string? fraction_string;
            string remarks;
            StringBuilder sb_convergents = new( );

            if( !initialFraction.IsNormal )
            {
                sb_continued_fraction.Append( "Undefined" );
                decimal_string = initialFraction.ToFloatString( cnc, 33 );
                fraction_string = initialFraction.ToRationalString( cnc, 33 );
                remarks = "⚠ The entered value is not a number.";
                sb_convergents.Append( "—\r\n" );
            }
            else
            {
                if( continued_fraction_items.Length <= 0 ) throw new ApplicationException( "The continued fraction is empty." );

                remarks = "";

                bool too_long = continued_fraction_items.Length > MAX_CONTINUED_FRACTION_ITEMS;

                sb_continued_fraction
                    .Append( "[ " )
                    .Append( continued_fraction_items[0].ToString( "D" ) );

                for( int i = 1; i < Math.Min( continued_fraction_items.Length, MAX_CONTINUED_FRACTION_ITEMS ); i++ )
                {
                    var item = continued_fraction_items[i];

                    sb_continued_fraction
                        .Append( i == 1 ? "; " : ", " )
                        .Append( item.ToString( "D" ) );
                }

                if( too_long )
                {
                    sb_continued_fraction.Append( " ... ]" );
                }
                else
                {
                    sb_continued_fraction.Append( " ]" );
                }

                if( too_long )
                {
                    remarks = $"{remarks}⚠ The continued fraction is too long.";
                }

                int convergent_number = 0;
                foreach( (BigInteger n, BigInteger d) p in ContinuedFractionUtilities.EnumerateContinuedFractionConvergents( continued_fraction_items ) )
                {
                    Fraction f = new( p.n, p.d );
                    string fs = f.ToFloatString( cnc, 20 );
                    bool fsa = fs.Contains( '≈' );
                    fs = fs.Replace( "≈", "" );

                    sb_convergents
                        .AppendLine( $"{convergent_number.ToString( ).PadLeft( 2, '\u2007' )}:\u2007{p.n:D} / {p.d:D} {( fsa ? '≈' : '=' )} {fs}" );

                    ++convergent_number;
                }

                // try to show more digits if it is a repeating decimal
                decimal_string = initialFraction.ToFloatString( cnc, 50 );
                if( !decimal_string.Contains( '(' ) ) decimal_string = initialFraction.ToFloatString( cnc, 20 );

                bool is_negative = initialFraction.IsNegative;
                BigInteger n = BigInteger.Abs( initialFraction.N );
                BigInteger d = initialFraction.D;
                BigInteger e = initialFraction.E;

                while( e < 0 )
                {
                    d *= 10;
                    ++e;

                    if( d.GetByteCount( ) > MAX_BIGINTEGER_BYTE_SIZE ) throw new ApplicationException( "The number exceeds the supported limits." );
                }

                while( e > 0 )
                {
                    n *= 10;
                    --e;

                    if( n.GetByteCount( ) > MAX_BIGINTEGER_BYTE_SIZE ) throw new ApplicationException( "The number exceeds the supported limits." );
                }

                fraction_string = $"{( is_negative ? -n : n ):D}";
                if( !e.IsZero ) fraction_string = $"{fraction_string}e{( e >= 0 ? "+" : "" )}{e:D}";
                if( !d.IsOne ) fraction_string = $"{fraction_string} / {d:D}";
            }

            Dispatcher.BeginInvoke( ( ) =>
            {
                runContinuedFraction.Text = sb_continued_fraction.ToString( );
                runDecimal.Text = decimal_string;
                runFraction.Text = fraction_string;
                runRemarks.Text = remarks;
                if( string.IsNullOrWhiteSpace( remarks ) )
                {
                    UIUtilities.ShowTopBlock( richTextBoxResults.Document, sectionContinuedFractionRemark, false, sectionContinuedFraction );
                }
                else
                {
                    UIUtilities.ShowTopBlock( richTextBoxResults.Document, sectionContinuedFractionRemark, true, sectionContinuedFraction );
                }
                runConvergents.Text = sb_convergents.ToString( );

                ShowOneRichTextBox( richTextBoxResults );
            } );
        }

        void ShowError( string errorText )
        {
            if( !Dispatcher.CheckAccess( ) )
            {
                Dispatcher.BeginInvoke( ( ) =>
                {
                    ShowError( errorText );
                } );
            }
            else
            {
                runError.Text = errorText;
                ShowOneRichTextBox( richTextBoxError );
                HideProgress( );
            }
        }

        void ShowOneRichTextBox( RichTextBox richTextBox )
        {
            bool was_visible = richTextBox.Visibility == Visibility.Visible;

            richTextBoxNote.Visibility = Visibility.Hidden;
            richTextBoxTypicalError.Visibility = Visibility.Hidden;
            richTextBoxError.Visibility = Visibility.Hidden;
            richTextBoxResults.Visibility = Visibility.Hidden;

            if( !was_visible ) richTextBox.ScrollToHome( );
            richTextBox.Visibility = Visibility.Visible;
        }

        #region Progress indicator

        void ShowProgress( )
        {
            mProgressTimer.Stop( );
            mProgressStatus = ProgressStatusEnum.None;

            if( mProgressShownTime != DateTime.MinValue )
            {
#if DEBUG
                Dispatcher.Invoke( ( ) =>
                {
                    Debug.Assert( labelPleaseWait.Visibility == Visibility.Visible );
                } );
#endif
                return;
            }
            else
            {
                mProgressStatus = ProgressStatusEnum.DelayToShow;
                mProgressTimer.Interval = DELAY_BEFORE_PROGRESS;
                mProgressTimer.Start( );
            }
        }

        void HideProgress( bool rightNow = false )
        {
            mProgressTimer.Stop( );
            mProgressStatus = ProgressStatusEnum.None;

            if( rightNow || mProgressShownTime == DateTime.MinValue )
            {
                Dispatcher.Invoke( ( ) => labelPleaseWait.Visibility = Visibility.Hidden );
                mProgressShownTime = DateTime.MinValue;
            }
            else
            {
#if DEBUG
                Dispatcher.Invoke( ( ) =>
                {
                    Debug.Assert( labelPleaseWait.Visibility == Visibility.Visible );
                } );
#endif

                TimeSpan elapsed = DateTime.Now - mProgressShownTime;

                if( elapsed >= MIN_DURATION_PROGRESS )
                {
                    Dispatcher.Invoke( ( ) => labelPleaseWait.Visibility = Visibility.Hidden );
                    mProgressShownTime = DateTime.MinValue;
                }
                else
                {
                    mProgressStatus = ProgressStatusEnum.DelayToHide;
                    mProgressTimer.Interval = MIN_DURATION_PROGRESS - elapsed;
                    mProgressTimer.Start( );
                }
            }
        }

        private void ProgressTimer_Tick( object? sender, EventArgs e )
        {
            mProgressTimer.Stop( );

            switch( mProgressStatus )
            {
            case ProgressStatusEnum.DelayToShow:
                labelPleaseWait.Visibility = Visibility.Visible;
                mProgressShownTime = DateTime.Now;
                break;
            case ProgressStatusEnum.DelayToHide:
                labelPleaseWait.Visibility = Visibility.Hidden;
                mProgressShownTime = DateTime.MinValue;
                break;
            case ProgressStatusEnum.None:
                //
                break;
            default:
                Debug.Assert( false );
                break;
            }

            mProgressStatus = ProgressStatusEnum.None;
        }

        #endregion

        [GeneratedRegex( """
            (?xni)^ \s* 
            (
             (
              (\+|(?<negative>-))? \s* (?<integer>\d+) 
              ((\s* \. \s* (?<floating>\d+)) | \.)? 
              (\s* \( \s* (?<repeating>\d+) \s* \) )? 
              (\s* [eE] \s* (\+|(?<negative_exponent>-))? \s* (?<exponent>\d+))? 
             )
            |
             (
              (\+|(?<negative>-))? \s* (?<nominator>\d+) 
              (\s* [eE] \s* (\+|(?<negative_exponent>-))? \s* (?<exponent>\d+))? 
              \s* / \s*
              (?<denominator>\d+) 
             )
            |
             (?<pi>pi | π)
            |
             (?<e>e)
                        )
            \s* $
            """, RegexOptions.IgnorePatternWhitespace
        )]
        private static partial Regex RegexToParseNumber( );
    }
}
