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
    /// Interaction logic for UCFromContinuedFraction.xaml
    /// </summary>
    public partial class UCFromContinuedFraction : UserControl
    {
        const int MAX_OUTPUT_DIGITS_DECIMAL = 250000; // (for example, the period of 6918696/2996677 has 241665 digits)
        const int MAX_OUTPUT_DIGITS_FRACTION = 200;
        const int MAX_OUTPUT_DIGITS_CONVERGENTS = 30;
        const int MAX_CONTINUED_FRACTION_ITEMS = 100;
        const int MAX_PERIODIC_CONTINUED_FRACTION_CONVERGENTS = 100;
        const int MAX_DIGITS = 300; // (for numerator and denominator)
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

        public UCFromContinuedFraction( )
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

        private void textBoxContinuedFraction_TextChanged( object sender, TextChangedEventArgs e )
        {
            if( !mLoaded ) return;

            RestartCalculationTimer( );
        }

        private void textBoxContinuedFraction_SelectionChanged( object sender, RoutedEventArgs e )
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
                textBoxContinuedFraction.Text = Properties.Settings.Default.LastContinuedFraction;

                textBoxContinuedFraction.Focus( );
                textBoxContinuedFraction.SelectAll( );
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
                Properties.Settings.Default.LastContinuedFraction = textBoxContinuedFraction.Text;

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

                (bool is_negative, IReadOnlyList<BigInteger>? continued_fraction, int period) = GetInputContinuedFraction( );
                if( continued_fraction == null ) return;

                mLastCancellable = new SimpleCancellable( );
                mCalculationThread = new Thread( ( ) =>
                {
                    CalculationThreadProc( mLastCancellable, is_negative, continued_fraction, period );
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

        (bool isNegative, IReadOnlyList<BigInteger>? list, int period) GetInputContinuedFraction( )
        {
            string input_text = textBoxContinuedFraction.Text;

            if( string.IsNullOrWhiteSpace( input_text ) )
            {
                ShowOneRichTextBox( richTextBoxNote );
                HideProgress( );

                return (false, null, 0);
            }

            Match m = RegexToParseContinuedFraction( ).Match( input_text );

            if( !m.Success )
            {
                ShowOneRichTextBox( richTextBoxTypicalError );
                HideProgress( );

                return (false, null, 0);
            }

            bool is_negative = m.Groups["is_negative"].Success;
            Group first_group = m.Groups["first"];

            Debug.Assert( first_group.Success );

            BigInteger first = BigInteger.Parse( first_group.Value );

            List<BigInteger> list = [first];

            Group next_group = m.Groups["next"];

            if( next_group.Success )
            {
                foreach( Capture c in next_group.Captures )
                {
                    BigInteger item = BigInteger.Parse( c.Value );

                    list.Add( item );
                }
            }

            int period = 0;
            Group left_par_group = m.Groups["lpar"];

            if( left_par_group.Success )
            {
                if( left_par_group.Index < first_group.Index )
                {
                    period = list.Count;
                }
                else
                {
                    (int Index, Capture Item) first_capture_after_left_par = next_group.Captures.Cast<Capture>( ).Index( ).FirstOrDefault( c => left_par_group.Index < c.Item.Index );

                    Debug.Assert( first_capture_after_left_par.Item != null );

                    period = list.Count - 1 - first_capture_after_left_par.Index;
                }

                Debug.Assert( period > 0 );
            }

            return (is_negative, list, period);
        }

        void CalculationThreadProc( ICancellable cnc, bool isNegative, IReadOnlyList<BigInteger> continuedFraction, int period )
        {
            try
            {
                CalculationContext ctx = new( cnc, MAX_DIGITS );
                bool is_periodic = period > 0;

                int max_convergents = !is_periodic ? int.MaxValue : ( Math.Max( continuedFraction.Count, MAX_PERIODIC_CONTINUED_FRACTION_CONVERGENTS ) + 1 );

                Fraction[] convergents =
                    ContinuedFractionUtilities
                        .EnumerateContinuedFractionConvergents( continuedFraction, period, max_convergents )
                        .Select( p =>
                                    p.d.IsZero ? p.n < 0 ? Fraction.NegativeInfinity : p.n > 0 ? Fraction.PositiveInfinity : Fraction.Undefined
                                    : new Fraction( p.d < 0 ? -p.n : p.n, BigInteger.Abs( p.d ) )
                                )
                        .ToArray( );

                cnc.TryThrow( );

                IReadOnlyList<BigInteger>? corrected_regular_continued_fraction = null;

                Fraction result;
                bool are_convergents_truncated = false;

                if( !is_periodic )
                {
                    result = convergents.Last( );
                }
                else
                {
                    // evaluate the convergence (empirical)

                    Fraction[] tail = convergents[^Math.Min( convergents.Length, Math.Max( period * 2, MAX_PERIODIC_CONTINUED_FRACTION_CONVERGENTS / 2 ) )..];
                    bool is_abnormal = false;

                    if( tail.Any( t => t.IsNormal ) && tail.Any( t => !t.IsNormal ) )
                    {
                        is_abnormal = true;
                    }
                    else
                    {
                        for( int i = 2; i < tail.Length; ++i )
                        {
                            cnc.TryThrow( );

                            Fraction a = tail[i - 2];
                            Fraction b = tail[i - 1];
                            Fraction c = tail[i];

                            Fraction diff_a_b = Fraction.Abs( Fraction.Sub( a, b, ctx ), ctx );
                            Fraction diff_b_c = Fraction.Abs( Fraction.Sub( b, c, ctx ), ctx );

                            if( diff_a_b.CompareTo( cnc, diff_b_c ) < 0 )
                            {
                                is_abnormal = true;

                                break;
                            }
                        }
                    }

                    if( convergents.Length > max_convergents )
                    {
                        convergents = convergents[0..max_convergents];
                        are_convergents_truncated = true;
                        result = convergents.Last( ).AsApprox( );
                    }
                    else
                    {
                        result = convergents.Last( );
                    }

                    if( is_abnormal ) result = Fraction.Undefined;
                }

                cnc.TryThrow( );

                if( isNegative ) result = Fraction.Neg( result, ctx );
                result = result.Simplify( ctx );

                if( result.IsNormal )
                {
                    BigInteger n = BigInteger.Abs( result.N );
                    BigInteger d = result.D;
                    BigInteger e = result.E;

                    Debug.Assert( d > 0 );

                    while( e < 0 )
                    {
                        d *= 10;
                        ++e;

                        if( d > ctx.MaxVal ) throw new ApplicationException( "The number exceeds the supported limits." );
                    }

                    Debug.Assert( n >= 0 );

                    cnc.TryThrow( );

                    while( e > 0 )
                    {
                        n *= 10;
                        --e;

                        if( n > ctx.MaxVal ) throw new ApplicationException( "The number exceeds the supported limits." );
                    }

                    Debug.Assert( e.IsZero );

                    cnc.TryThrow( );

                    if( !is_periodic )
                    {
                        BigInteger[] continued_fraction_items =
                            [.. ContinuedFractionUtilities
                            .EnumerateContinuedFraction( BigInteger.Abs(result.N), result.D )
                            .Take( MAX_CONTINUED_FRACTION_ITEMS + 1 )];

                        cnc.TryThrow( );

                        if( continued_fraction_items.Length < MAX_CONTINUED_FRACTION_ITEMS )
                        {
                            if( result.IsNegative ) continued_fraction_items = ContinuedFractionUtilities.Negate( continued_fraction_items );

                            bool is_not_regular = !continued_fraction_items.SequenceEqual( continuedFraction );

                            if( is_not_regular )
                            {
                                corrected_regular_continued_fraction = continued_fraction_items;
                            }
                        }
                    }
                    else
                    {
                        // TODO: implement the correction in case of periodic continued fractions
                        Debug.Assert( corrected_regular_continued_fraction == null );
                    }
                }

                string? error_text = null;

                if( error_text == null )
                {
                    ShowResults( cnc, result, convergents, corrected_regular_continued_fraction, are_convergents_truncated, is_periodic );

                    HideProgress( );
                }

                if( !string.IsNullOrEmpty( error_text ) )
                {
                    ShowError( error_text );
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

        void ShowResults( ICancellable cnc, Fraction result, Fraction[] convergents, IReadOnlyList<BigInteger>? correctedRegularContinuedFraction, bool areConvergentsTruncated, bool isPeriodic )
        {
            bool is_corrected = correctedRegularContinuedFraction != null;
            bool is_normal = result.IsNormal;
            bool is_negative = result.IsNegative;

            string result_as_decimal = result.ToFloatString( cnc, MAX_OUTPUT_DIGITS_DECIMAL );
            bool is_decimal_approx = result_as_decimal.StartsWith( '≈' );

            string result_as_fraction;

#if true
            if( !is_normal )
            {
                result_as_fraction = result.ToRationalString( cnc, 20 ); // 
            }
            else
            {
                BigInteger n = BigInteger.Abs( result.N );
                BigInteger d = result.D;
                BigInteger e = result.E;

                CalculationContext ctx = new( cnc, MAX_DIGITS );

                Debug.Assert( d > 0 );

                while( e < 0 )
                {
                    d *= 10;
                    ++e;

                    if( d > ctx.MaxVal ) throw new ApplicationException( "The number exceeds the supported limits." );
                }

                Debug.Assert( n >= 0 );

                while( e > 0 )
                {
                    n *= 10;
                    --e;

                    if( n > ctx.MaxVal ) throw new ApplicationException( "The number exceeds the supported limits." );
                }

                Debug.Assert( e.IsZero );

                result_as_fraction = $"{( is_negative ? -n : n ):D}";
                if( !e.IsZero ) result_as_fraction = $"{result_as_fraction}e{( e >= 0 ? "+" : "" )}{e:D}";
                if( !d.IsOne ) result_as_fraction = $"{result_as_fraction} / {d:D}";

                if( result.IsApprox ) result_as_fraction = $"≈{result_as_fraction}";
            }
#else
            result_as_fraction = result.ToRationalString( cnc, MAX_OUTPUT_DIGITS_FRACTION );
#endif

            StringBuilder sb_convergents = new( );
            string convergents_title;

            int convergent_index = 0;
            foreach( Fraction f in convergents )
            {
                sb_convergents
                    .Append( $"{convergent_index.ToString( ).PadLeft( 2, '\u2007' )}:\u2007" ); // U+2007 FIGURE SPACE

                if( !f.IsNormal )
                {
                    sb_convergents
                        .AppendLine( f.ToRationalString( cnc, MAX_OUTPUT_DIGITS_CONVERGENTS ) );
                }
                else
                {
                    Debug.Assert( f.E == 0 );

                    string fs = f.ToFloatString( cnc, MAX_OUTPUT_DIGITS_CONVERGENTS );
                    bool fsa = fs.Contains( '≈' );
                    fs = fs.Replace( "≈", "" );

                    if( convergent_index == 0 && f.D.IsOne )
                    {
                        sb_convergents
                            .AppendLine( $"{( fsa ? "≈" : "" )}{fs}" );
                    }
                    else
                    {
                        sb_convergents
                            .AppendLine( $"{f.N:D} / {f.D:D} {( fsa ? '≈' : '=' )} {fs}" );
                    }
                }

                ++convergent_index;
            }

            if( areConvergentsTruncated ) sb_convergents.AppendLine( ". . ." );

            StringBuilder sb_corrected = new( );

            if( is_corrected )
            {
                convergents_title = "Convergents of entered continued fraction";

                sb_corrected
                    .Append( "[ " )
                    .Append( correctedRegularContinuedFraction![0].ToString( "D" ) );

                for( int i = 1; i < correctedRegularContinuedFraction.Count; i++ )
                {
                    var item = correctedRegularContinuedFraction[i];

                    sb_corrected
                        .Append( i == 1 ? "; " : ", " )
                        .Append( item.ToString( "D" ) );
                }

                sb_corrected.Append( " ]" );
            }
            else
            {
                convergents_title = "Convergents";
            }

            Dispatcher.BeginInvoke( ( ) =>
                {
                    runDecimal.Text = result_as_decimal;
                    runFraction.Text = result_as_fraction;
                    runCorrected.Text = sb_corrected.ToString( );
                    runConvergents.Text = sb_convergents.ToString( );
                    runConvergentsTitle.Text = convergents_title;

                    UIUtilities.ShowTopBlock( richTextBoxResults.Document, sectionInfo, is_normal && !is_corrected && !isPeriodic, sectionFraction );
                    UIUtilities.ShowTopBlock( richTextBoxResults.Document, sectionWarning, is_normal && is_corrected && !isPeriodic, sectionFraction );
                    UIUtilities.ShowTopBlock( richTextBoxResults.Document, sectionCorrected, is_normal && is_corrected && !isPeriodic, sectionFraction, sectionWarning );

                    ShowOneRichTextBox( richTextBoxResults );

                    {
                        // adjust page width to avoid wrapping

                        string text = new TextRange( richTextBoxResults.Document.ContentStart, richTextBoxResults.Document.ContentEnd ).Text;
                        FormattedText ft = new( text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                            new Typeface( richTextBoxResults.FontFamily, richTextBoxResults.FontStyle, FontWeights.Bold, richTextBoxResults.FontStretch ), richTextBoxResults.FontSize, Brushes.Black, VisualTreeHelper.GetDpi( richTextBoxResults ).PixelsPerDip );

                        richTextBoxResults.Document.PageWidth = ft.Width + 100;
                    }
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
                if( richTextBoxError.Visibility == Visibility.Visible || richTextBoxTypicalError.Visibility == Visibility.Visible )
                {
                    ShowOneRichTextBox( richTextBoxNote );
                }
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
            (?xni)
            ^
            \s* ((\+|(?<is_negative>-))? \s* (?<lbr>\[)? \s* (?<lpar>\()? (?(lbr)|(?(lpar)|(?!))) )? \s*
            (?<first>[\-\+]?\d+ \s*) 
            (([,;]\s*|\s+) (?<lpar>(?(lpar)(?!)|\(\s*))? (?<next>[\-\+]?\d+) \s*)*
            ([,;])? \s*
            (?(lpar)\)) \s*
            \]? \s*
            $
            """, RegexOptions.IgnorePatternWhitespace, 20_000
        )]
        private static partial Regex RegexToParseContinuedFraction( );
    }
}
